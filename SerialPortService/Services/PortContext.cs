using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
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
    /// Pipeline 循环实现见 PortContext.Pipeline.cs，重连逻辑见 PortContext.Reconnect.cs。
    /// </summary>
    public abstract partial class PortContext<T> : IPortContext where T : class
    {
        private const bool EnableRawReadChunkLog = false;
        private const int OpenRetryAttempts = 50;
        private const int OpenRetryDelayMs = 100;

        /// <summary>
        /// 日志记录器
        /// </summary>
        protected readonly ILogger Logger;

        /// <summary>
        /// 内存块包装器，用于在 Channel 中传递借来的数组
        /// </summary>
        internal readonly struct RentedBuffer : IDisposable
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
                    System.Buffers.ArrayPool<byte>.Shared.Return(Buffer);
                }
            }
        }

        /// <summary>
        /// 具体串口对象
        /// </summary>
        private readonly System.IO.Ports.SerialPort _port;

        #region 数据缓冲区
        // 发送数据缓冲通道
        private Channel<DataPacket> _sendChannel;

        // 原始数据缓冲通道 (生产者-消费者 模型)
        private Channel<RentedBuffer> _rawInputChannel;
        #endregion

        #region 任务
        private Task? _ioTask;
        private Task? _parseTask;
        private Task? _sendTask;
        private Task? _rawBytesLoggerTask;
        #endregion

        /// <summary>
        /// 任务令牌
        /// </summary>
        private CancellationTokenSource? _cts;
        private int _closeSignaled;
        private int _disposeSignaled;

        // 诊断：按分钟统计原始字节
        private long _rawBytesSinceLastLog;
        private long _rawReadChunkSeq;
        private long _rawReadByteTotal;
        private volatile bool _lastCloseSucceeded = true;

        private volatile bool _isRunning = false;
        private long _reconnectCycleCount;
        private long _reconnectExhaustedCount;

        public string Name => _port.PortName;
        public bool LastCloseSucceeded => _lastCloseSucceeded;

        /// <summary>
        /// 最后发送的数据 (供子类解析器使用)
        /// </summary>
        protected byte[]? _lastSent;

        public event EventHandler<object>? OnHandleChanged;

        protected virtual void OnParsed(T result) { }

        protected abstract IStreamParser<T> Parser { get; }

        public PortContext(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
        {
            Logger = logger;
            _port = new System.IO.Ports.SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadBufferSize = 1024 * 1024,
                ReadTimeout = System.IO.Ports.SerialPort.InfiniteTimeout,
            };
            _sendChannel = CreateSendChannel();
            _rawInputChannel = CreateRawInputChannel();
        }

        private static Channel<DataPacket> CreateSendChannel()
        {
            return Channel.CreateBounded<DataPacket>(new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }

        private static Channel<RentedBuffer> CreateRawInputChannel()
        {
            return Channel.CreateBounded<RentedBuffer>(new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        }

        private async Task EnsurePortOpenedWithRetryAsync()
        {
            if (_port.IsOpen)
            {
                return;
            }

            Exception? lastException = null;
            for (var attempt = 1; attempt <= OpenRetryAttempts; attempt++)
            {
                try
                {
                    _port.Open();
                    lastException = null;
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                    if (attempt < OpenRetryAttempts)
                    {
                        // 步骤1：使用异步延迟替代 Thread.Sleep。
                        // 为什么：避免阻塞调用线程（可能是 UI 线程）。
                        // 风险点：Thread.Sleep 会冻结 UI 导致"打开串口卡死"。
                        await Task.Delay(OpenRetryDelayMs).ConfigureAwait(false);
                    }
                }
            }

            if (lastException != null)
            {
                throw new InvalidOperationException($"串口 {Name} 打开失败：设备仍被占用，请确认已完全关闭后重试", lastException);
            }
        }

        private void StartPipelineTasks(CancellationToken token)
        {
            _ioTask = Task.Run(() => IoReadLoopAsync(token));
            _parseTask = Task.Run(() => ParseLoop(token));
            _sendTask = Task.Run(() => SendLoop(token));
            _rawBytesLoggerTask = Task.Run(() => RawBytesLoggerLoop(token));
        }

        private void StopAndCompleteChannels()
        {
            _isRunning = false;
            _cts?.Cancel();
            _sendChannel.Writer.TryComplete();
            _rawInputChannel.Writer.TryComplete();
        }

        private void TryCloseSerialPort()
        {
            if (!_port.IsOpen)
            {
                return;
            }

            var closeTask = Task.Run(() =>
            {
                try { _port.DiscardInBuffer(); } catch { }
                try { _port.Close(); } catch { }
            });

            if (closeTask.Wait(1000))
            {
                return;
            }

            _lastCloseSucceeded = false;
            Logger.AddLog(LogLevel.Warning, $"[{Name}] Serial close timeout, continue shutdown");

            var forceDisposeTask = Task.Run(() =>
            {
                try { _port.Dispose(); } catch { }
            });

            if (!forceDisposeTask.Wait(3000))
            {
                _lastCloseSucceeded = false;
                Logger.AddLog(LogLevel.Warning, $"[{Name}] Serial force-dispose timeout, port handle may still be occupied");
            }
        }

        private void WaitPipelineTasksForStop()
        {
            if (_ioTask == null && _parseTask == null && _sendTask == null && _rawBytesLoggerTask == null)
            {
                return;
            }

            var tasks = new List<Task>();
            if (_ioTask != null) tasks.Add(_ioTask);
            if (_parseTask != null) tasks.Add(_parseTask);
            if (_sendTask != null) tasks.Add(_sendTask);
            if (_rawBytesLoggerTask != null) tasks.Add(_rawBytesLoggerTask);

            try
            {
                var completed = Task.WaitAll(tasks.ToArray(), 2000);
                if (!completed)
                {
                    _lastCloseSucceeded = false;
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Pipeline stop timeout, forcing serial port close");
                }
            }
            catch (AggregateException ex)
            {
                _lastCloseSucceeded = false;
                Logger.AddLog(LogLevel.Warning, $"[{Name}] Pipeline stop raised exception: {ex.Flatten().Message}", exception: ex);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 异步打开串口并启动处理引擎（推荐）。
        /// </summary>
        public async Task OpenAsync()
        {
            // 步骤1：幂等保护，避免重复启动管线。
            // 为什么：重复启动会创建多组读写任务导致竞争。
            // 风险点：多任务并发读写同一串口会触发不可预期错误。
            if (_isRunning) return;
            Interlocked.Exchange(ref _closeSignaled, 0);

            // 步骤2：异步重试打开串口，真正 await 而非阻塞线程。
            // 为什么：避免在有 SynchronizationContext 的线程上死锁。
            // 风险点：调用方需确保不在持锁上下文中 await。
            await EnsurePortOpenedWithRetryAsync().ConfigureAwait(false);

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _sendChannel = CreateSendChannel();
            _rawInputChannel = CreateRawInputChannel();

            // 步骤3：按"读-解析-发送"顺序启动后台任务。
            // 为什么：拆分职责便于背压控制与性能观测。
            // 风险点：任一任务未启动会造成数据链路断裂。
            StartPipelineTasks(_cts.Token);

            Logger.AddLog(LogLevel.Information, $"[{Name}] Pipeline 引擎已启动");
        }

        /// <summary>
        /// 同步打开串口并启动处理引擎（向后兼容）。
        /// 内部通过 <see cref="Task.Run"/> 委托到线程池避免 SynchronizationContext 死锁。
        /// 新代码建议优先使用 <see cref="OpenAsync"/>。
        /// </summary>
        public void Open()
        {
            if (_isRunning) return;
            // 步骤1：通过 Task.Run 跳过 SynchronizationContext 再同步等待。
            // 为什么：WPF/WinForms UI 线程有 SyncContext，直接 .GetAwaiter().GetResult() 会死锁。
            // 风险点：新代码建议直接使用 OpenAsync()，此方法仅保留向后兼容。
            Task.Run(() => OpenAsync()).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closeSignaled, 1) == 1)
            {
                return;
            }

            _lastCloseSucceeded = true;

            // 步骤1：先标记停止并关闭通道写端。
            // 为什么：通知后台任务尽快退出循环。
            // 风险点：若不先停写，关闭期间可能继续有新消息进入。
            StopAndCompleteChannels();

            // 步骤2：优先关闭底层串口句柄以打断阻塞读写。
            // 为什么：部分驱动下 ReadAsync 取消不敏感，需关闭句柄触发退出。
            // 风险点：若先等待任务再关句柄，UI 线程可能长时间阻塞。
            TryCloseSerialPort();

            // 步骤3：等待后台任务有序结束。
            // 为什么：尽量避免在任务运行中直接关闭串口句柄。
            // 风险点：强行关闭会导致对象释放异常和数据中断。
            WaitPipelineTasksForStop();
        }

        /// <summary>
        /// 业务层入口：入队异步发送，不阻塞业务线程。
        /// </summary>
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
            await _sendChannel.Writer.WriteAsync(packet, default).ConfigureAwait(false);
            return data;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public virtual void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeSignaled, 1) == 1)
            {
                return;
            }

            Close();

            // 步骤1：限时释放串口对象。
            // 为什么：部分驱动在异常态下 Dispose 可能长时间阻塞。
            // 风险点：若在 UI 线程无保护调用，可能表现为"关闭串口卡死"。
            var disposeTask = Task.Run(() =>
            {
                try { _port?.Dispose(); } catch { }
            });

            if (!disposeTask.Wait(1000))
            {
                _lastCloseSucceeded = false;
                Logger.AddLog(LogLevel.Warning, $"[{Name}] Serial dispose timeout, continue cleanup");
            }

            _cts?.Dispose();
        }
    }
}