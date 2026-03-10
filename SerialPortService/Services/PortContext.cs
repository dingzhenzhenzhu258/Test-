using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SerialPortService.Services
{
    /// <summary>
    /// 串口上下文基类。
    /// 负责串口生命周期管理与数据流水线调度：
    /// <list type="number">
    /// <item><description>异步读取串口字节流（IO 任务）</description></item>
    /// <item><description>解析字节流为业务对象（解析任务）</description></item>
    /// <item><description>异步发送业务请求（发送任务）</description></item>
    /// </list>
    /// 子类仅需提供解析器，并可通过 <see cref="OnParsed(T)"/> 扩展处理逻辑。
    /// </summary>
    public abstract class PortContext<T> : IPortContext where T : class
    {
        /// <summary>
        /// 日志记录器
        /// </summary>
        protected readonly ILogger Logger;

        /// <summary>
        /// 内存块包装器，用于在 Channel 中传递借来的数组
        /// </summary>
        private readonly struct RentedBuffer : IDisposable
        {
            public readonly byte[] Buffer;
            public readonly int Length;

            public RentedBuffer(byte[] buffer, int length)
            {
                Buffer = buffer;
                Length = length;
            }

            public void Dispose()
            {
                if (Buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(Buffer);
                }
            }
        }

        /// <summary>
        /// 具体串口对象
        /// </summary>
        private readonly SerialPort _port;

        #region 数据缓冲区
        // 发送数据缓冲通道
        private readonly Channel<DataPacket> _sendChannel = Channel.CreateUnbounded<DataPacket>();

        // 原始数据缓冲通道 (生产者-消费者 模型) 连接 IO 线程和解析任务的传送带，起到削峰填谷和流控的作用
        // 优化：改为传递 RentedBuffer 避免频繁分配
        private readonly Channel<RentedBuffer> _rawInputChannel; 
        #endregion

        #region 任务
        // 专用的 IO 读取任务，负责从硬件异步读取并写入通道
        private Task? _ioTask;

        // 逻辑解析任务
        private Task? _parseTask;
        private Task? _sendTask;

        #endregion

        /// <summary>
        /// 任务令牌
        /// </summary>
        private CancellationTokenSource? _cts;

        /// <summary>
        /// 运行状态
        /// </summary>
        private volatile bool _isRunning = false;
        private long _reconnectCycleCount;
        private long _reconnectExhaustedCount;

        /// <summary>
        /// 串口名
        /// </summary>
        public string Name => _port.PortName;

        /// <summary>
        /// 最后发送的数据 (供子类解析器使用)
        /// </summary>
        protected byte[]? _lastSent;

        /// <summary>
        /// 处理器事件
        /// </summary>
        public event EventHandler<object>? OnHandleChanged;

        /// <summary>
        /// 解析成功后的内部回调（供子类进行高性能处理，避免依赖事件回调）
        /// </summary>
        /// <param name="result">解析结果</param>
        protected virtual void OnParsed(T result) { }

        /// <summary>
        /// 解析器
        /// </summary>
        protected abstract IStreamParser<T> Parser { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="portName"></param>
        /// <param name="baudRate"></param>
        /// <param name="parity"></param>
        /// <param name="dataBits"></param>
        /// <param name="stopBits"></param>
        /// <param name="logger"></param>
        public PortContext(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
        {
            Logger = logger;
            // 初始化串口
            _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadBufferSize = 1024 * 1024,
                ReadTimeout = SerialPort.InfiniteTimeout,
            };

            // 配置背压通道：容量为 500 个数据块 
            _rawInputChannel = Channel.CreateBounded<RentedBuffer>(new BoundedChannelOptions(500)
            {
                // 数据满时的行为：等待
                FullMode = BoundedChannelFullMode.Wait,
                // 单一读写者，优化性能
                SingleReader = true,
                // 单一写者，优化性能
                SingleWriter = true
            });
        }

        /// <summary>
        /// 打开串口 并启动处理引擎
        /// </summary>
        public void Open()
        {
            // 步骤1：幂等保护，避免重复启动管线。
            // 为什么：重复启动会创建多组读写任务导致竞争。
            // 风险点：多任务并发读写同一串口会触发不可预期错误。
            if (_isRunning) return;
            if (!_port.IsOpen) _port.Open();

            _isRunning = true;
            _cts = new CancellationTokenSource();

            // 1. 启动 IO 读取任务 (异步读取 BaseStream，避免轮询与 Thread.Sleep)
            // 步骤2：按“读-解析-发送”顺序启动后台任务。
            // 为什么：拆分职责便于背压控制与性能观测。
            // 风险点：任一任务未启动会造成数据链路断裂。
            _ioTask = Task.Run(() => IoReadLoopAsync(_cts.Token));

            // 2. 启动 解析任务 (消费原始数据 -> 状态机)
            _parseTask = Task.Run(() => ParseLoop(_cts.Token));

            // 3. 启动 发送任务 (保持你原有的逻辑)
            _sendTask = Task.Run(() => SendLoop(_cts.Token));

            Logger.AddLog(LogLevel.Information, $"[{Name}] Pipeline 引擎已启动");
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close()
        {
            // 步骤1：先标记停止并关闭通道写端。
            // 为什么：通知后台任务尽快退出循环。
            // 风险点：若不先停写，关闭期间可能继续有新消息进入。
            _isRunning = false;
            _sendChannel.Writer.TryComplete();
            _rawInputChannel.Writer.TryComplete();
            _cts?.Cancel();

            // 步骤2：等待后台任务有序结束。
            // 为什么：尽量避免在任务运行中直接关闭串口句柄。
            // 风险点：强行关闭会导致对象释放异常和数据中断。
            if (_ioTask != null || _parseTask != null || _sendTask != null)
            {
                var tasks = new List<Task>();
                if (_ioTask != null) tasks.Add(_ioTask);
                if (_parseTask != null) tasks.Add(_parseTask);
                if (_sendTask != null) tasks.Add(_sendTask);
                try
                {
                    var completed = Task.WaitAll(tasks.ToArray(), 2000);
                    if (!completed)
                    {
                        Logger.AddLog(LogLevel.Warning, $"[{Name}] Pipeline stop timeout, forcing serial port close");
                    }
                }
                catch (AggregateException ex)
                {
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Pipeline stop raised exception: {ex.Flatten().Message}", exception: ex);
                }
                catch
                {
                }
            }

            // 步骤3：关闭底层串口句柄。
            // 为什么：释放系统资源并允许后续重新打开。
            // 风险点：未关闭会造成端口被占用。
            if (_port.IsOpen)
            {
                try
                {
                    _port.DiscardInBuffer();
                }
                catch (Exception ex)
                {
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] DiscardInBuffer failed: {ex.Message}", exception: ex);
                }

                try
                {
                    _port.Close();
                }
                catch (Exception ex)
                {
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Close failed: {ex.Message}", exception: ex);
                }
            }
        }

        /// <summary>
        /// Stage 1: IO 异步读取任务 (Producer)
        /// </summary>
        private async Task IoReadLoopAsync(CancellationToken token)
        {
            const int readSize = 4096;

            // 只要没停止就持续读取
            while (_isRunning && !token.IsCancellationRequested)
            {
                byte[]? buffer = null;
                try
                {
                    if (!_port.IsOpen)
                    {
                        if (!await TryReconnectAsync(token, "port closed").ConfigureAwait(false))
                        {
                            break;
                        }
                        continue;
                    }

                    buffer = ArrayPool<byte>.Shared.Rent(readSize);
                    int count = await _port.BaseStream.ReadAsync(buffer.AsMemory(0, readSize), token).ConfigureAwait(false);

                    if (count > 0)
                    {
                        var rented = new RentedBuffer(buffer, count);
                        buffer = null;

                        // 写入通道，如果满了会异步等待 (自动流控)
                        if (!_rawInputChannel.Writer.TryWrite(rented))
                        {
                            try
                            {
                                await _rawInputChannel.Writer.WriteAsync(rented, token).ConfigureAwait(false);
                            }
                            catch (ChannelClosedException)
                            {
                                rented.Dispose();
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                rented.Dispose();
                                break;
                            }
                            catch
                            {
                                rented.Dispose();
                                throw;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning && !token.IsCancellationRequested)
                    {
                        Logger.AddLog(LogLevel.Error, $"[IO Error] {ex.Message}", exception: ex);
                        if (!await TryReconnectAsync(token, "read failed").ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    if (buffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        }

        /// <summary>
        /// 专门负责从通道拿数据，运行状态机解析出完整的业务对象
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        // --- Stage 2: 解析任务 (Consumer) ---
        private async Task ParseLoop(CancellationToken token)
        {
            try
            {
                // 复用列表，减少分配
                var resultList = new List<T>();

                await foreach (var chunk in _rawInputChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        // 这里可以体现"逐字节"的思想：
                        // 虽然我们拿到了一个 chunk (Span)，但我们将其视为一个字节流
                        // 真正的"逐字节状态机"逻辑在 Parser.Parse 内部实现
                        
                        // 批量喂给解析器 (使用 Span 切片)
                        Parser.Parse(chunk.Buffer.AsSpan(0, chunk.Length), resultList);

                        if (resultList.Count > 0)
                        {
                            foreach (var result in resultList)
                            {
                                OnParsed(result);
                                var handler = OnHandleChanged;
                                if (handler != null)
                                {
                                    // 仅在存在订阅者时才创建事件对象并触发
                                    var operateResult = new OperateResult<T>(result, true, "Success");
                                    handler(this, operateResult);
                                }
                            }
                            resultList.Clear();
                        }
                    }
                    finally
                    {
                        // 消费完毕，必须归还数组到池中！
                        chunk.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// 业务层调用 Send() 时只是把数据扔进这个队列瞬间返回，不会因为串口写入慢而阻塞业务线程
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<byte[]> Send(byte[] data)
        {
            // 步骤1：发送前进行数据与状态校验。
            // 为什么：在进入发送队列前尽早返回明确失败原因。
            // 风险点：无校验会把无效数据带入发送管线。
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("发送数据不能为空", nameof(data));
            }

            if (!_isRunning || !_port.IsOpen)
            {
                throw new InvalidOperationException($"串口 {Name} 未打开，发送失败");
            }

            // 步骤2：入队交给发送任务异步写入。
            // 为什么：避免业务线程直接阻塞在串口 IO。
            // 风险点：若发送队列失控，可能造成内存压力上升。
            var packet = new DataPacket(data);
            await _sendChannel.Writer.WriteAsync(packet).ConfigureAwait(false);
            return data;
        }

        /// <summary>
        /// 写入串口发送循环
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task SendLoop(CancellationToken token)
        {
            try
            {
                await foreach (var msg in _sendChannel.Reader.ReadAllAsync(token))
                {
                    // 打印发送的原始数据 (Hex)
                    // Logger.LogTrace("[IO Write] {Data}", BitConverter.ToString(msg.Data));

                    if (!_port.IsOpen && !await TryReconnectAsync(token, "write path detected closed port").ConfigureAwait(false))
                    {
                        Logger.AddLog(LogLevel.Error, $"[IO Write] Send dropped due to reconnect failure. Port={Name}, Bytes={msg.Data.Length}");
                        continue;
                    }

                    try
                    {
                        await _port.BaseStream.WriteAsync(msg.Data.AsMemory(0, msg.Data.Length), token).ConfigureAwait(false);
                        _lastSent = msg.Data;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        Logger.AddLog(LogLevel.Error, $"[IO Write] {ex.Message}", exception: ex);
                        await TryReconnectAsync(token, "write failed").ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Logger.AddLog(LogLevel.Error, $"[IO Write] {ex.Message}", exception: ex);
            }
        }

        private async Task<bool> TryReconnectAsync(CancellationToken token, string reason)
        {
            var maxAttempts = SerialPortReconnectPolicy.MaxReconnectAttempts;
            var intervalMs = SerialPortReconnectPolicy.ReconnectIntervalMs;

            // 步骤1：按最大次数执行重连尝试。
            // 为什么：给短暂链路故障留恢复窗口。
            // 风险点：无限重试会造成线程长期占用。
            for (int attempt = 1; attempt <= maxAttempts && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    if (!_isRunning) return false;

                    if (_port.IsOpen)
                    {
                        return true;
                    }

                    // 步骤2：重连成功后立即记录结果并返回。
                    // 为什么：尽快恢复读写链路，减少业务中断。
                    // 风险点：成功后不及时返回会产生多余重连动作。
                    _port.Open();
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Reconnected successfully. Reason={reason}, Attempt={attempt}/{maxAttempts}");
                    ObserveReconnectOutcome(isExhausted: false);
                    return true;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Reconnect failed. Reason={reason}, Attempt={attempt}/{maxAttempts}, Error={ex.Message}");
                }

                // 步骤3：失败后按间隔退避。
                // 为什么：避免连续重试造成设备或总线压力。
                // 风险点：无退避会触发重连风暴。
                if (attempt < maxAttempts)
                {
                    try
                    {
                        await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }

            // 步骤4：重连耗尽后记录失败告警。
            // 为什么：给上层提供可观测故障信号。
            // 风险点：无耗尽告警会导致故障长期隐蔽。
            Logger.AddLog(LogLevel.Error, $"[{Name}] Reconnect exhausted. Reason={reason}, MaxAttempts={maxAttempts}");
            ObserveReconnectOutcome(isExhausted: true);
            return false;
        }

        private void ObserveReconnectOutcome(bool isExhausted)
        {
            var total = Interlocked.Increment(ref _reconnectCycleCount);
            if (isExhausted)
            {
                Interlocked.Increment(ref _reconnectExhaustedCount);
            }

            var thresholdPercent = SerialPortReconnectPolicy.ReconnectFailureRateAlertThresholdPercent;
            var minSamples = SerialPortReconnectPolicy.ReconnectFailureRateAlertMinSamples;
            if (thresholdPercent <= 0 || minSamples <= 0)
            {
                return;
            }

            if (total < minSamples || total % minSamples != 0)
            {
                return;
            }

            var exhausted = Interlocked.Read(ref _reconnectExhaustedCount);
            var failureRatePercent = (double)exhausted * 100d / total;
            if (failureRatePercent >= thresholdPercent)
            {
                Logger.AddLog(LogLevel.Error, $"[{Name}] Reconnect failure-rate alert: {failureRatePercent:F2}% (exhausted={exhausted}, total={total}, threshold={thresholdPercent}%)");
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public virtual void Dispose()
        {
            Close();
            _port?.Dispose();
            _cts?.Dispose();
        }
    }
}
