using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 通用串口处理器。
    /// 该类封装了请求/响应模型的公共能力：
    /// <list type="bullet">
    /// <item><description>有界响应通道与满载策略</description></item>
    /// <item><description>超时重试与取消令牌</description></item>
    /// <item><description>主动上报分流与不匹配统计</description></item>
    /// <item><description>运行指标快照与上报注册</description></item>
    /// </list>
    /// 新协议建议通过“解析器 + 匹配策略”复用该类，而非重复实现收发控制逻辑。
    /// </summary>
    /// <typeparam name="T">解析结果类型</typeparam>
    public class GenericHandler<T> : PortContext<T>, IGenericHandlerMetricsProvider where T : class
    {
        private readonly IStreamParser<T> _parser;
        private readonly GenericHandlerOptions _options;
        private readonly IResponseMatcher<T>? _matcher;
        private readonly string _handlerName;

        // 统一通道模型：有界 + 限流，适配高并发采集
        private readonly Channel<T> _responseChannel;
        private readonly Channel<T> _parsedPacketChannel;
        private readonly Channel<T>? _waitModeQueue;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly Task? _waitModeWriterTask;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private int _activeRequests;
        private long _idleDroppedCount;
        private long _overflowDroppedCount;
        private long _unmatchedCount;
        private long _retryCount;
        private long _timeoutCount;
        private long _matchedCount;
        private long _totalLatencyMs;
        private long _waitModeQueueLength;
        private long _waitModeQueueHighWatermark;
        private long _parsedPacketDropCount;
        private long _lastParsedUtcTicks;
        private int _consecutiveTimeoutCount;
        private readonly int _metricsRegistrationId;
        private int _disposeSignaled;
        private int _waitBacklogAlertActive;

        /// <summary>
        /// 创建通用处理器实例。
        /// </summary>
        /// <param name="portName">串口名称（例如 COM3）</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="parity">校验位</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="stopBits">停止位</param>
        /// <param name="parser">字节流解析器</param>
        /// <param name="logger">日志实例</param>
        /// <param name="options">通用处理参数</param>
        /// <param name="matcher">响应匹配策略（可空）</param>
        public GenericHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser, ILogger logger, GenericHandlerOptions? options = null, IResponseMatcher<T>? matcher = null)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _options = options ?? new GenericHandlerOptions();
            _matcher = matcher;
            _handlerName = GetType().Name;

            if (_options.ResponseChannelCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "ResponseChannelCapacity must be greater than 0");
            if (_options.SampleLogInterval < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "SampleLogInterval must be >= 0");
            if (_options.WaitModeQueueCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "WaitModeQueueCapacity must be > 0");

            _responseChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.ResponseChannelCapacity)
            {
                FullMode = _options.ResponseChannelFullMode,
                SingleReader = true,
                SingleWriter = false
            });

            _parsedPacketChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.ResponseChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

            if (_options.ResponseChannelFullMode == BoundedChannelFullMode.Wait)
            {
                _waitModeQueue = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.WaitModeQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = true
                });
                _waitModeWriterTask = Task.Run(() => DrainWaitModeQueueAsync(_disposeCts.Token));
            }

            _metricsRegistrationId = GenericHandlerMetricsPublisher.Register(this);
        }

        ~GenericHandler()
        {
            GenericHandlerMetricsPublisher.Unregister(_metricsRegistrationId);
        }

        string IGenericHandlerMetricsProvider.HandlerName => _handlerName;
        string IGenericHandlerMetricsProvider.PortName => Name;
        string IGenericHandlerMetricsProvider.ProtocolName => _options.ProtocolTag ?? _parser.GetType().Name;
        string IGenericHandlerMetricsProvider.DeviceType => _options.DeviceTypeTag ?? _handlerName;

        /// <summary>
        /// 释放处理器资源并执行指标显式注销。
        /// </summary>
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeSignaled, 1) == 0)
            {
                _responseChannel.Writer.TryComplete();
                _parsedPacketChannel.Writer.TryComplete();
                _disposeCts.Cancel();
                _waitModeQueue?.Writer.TryComplete();
                try
                {
                    _waitModeWriterTask?.GetAwaiter().GetResult();
                }
                catch
                {
                }

                GenericHandlerMetricsPublisher.Unregister(_metricsRegistrationId);
                GC.SuppressFinalize(this);
            }

            base.Dispose();
            _disposeCts.Dispose();
            _semaphore.Dispose();
        }

        private async Task DrainWaitModeQueueAsync(CancellationToken cancellationToken)
        {
            if (_waitModeQueue == null)
            {
                return;
            }

            try
            {
                await foreach (var item in _waitModeQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var backlog = Interlocked.Decrement(ref _waitModeQueueLength);
                    if (_options.WaitBacklogAlertThreshold > 0 && backlog < _options.WaitBacklogAlertThreshold)
                    {
                        Interlocked.Exchange(ref _waitBacklogAlertActive, 0);
                    }
                    await _responseChannel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }

        protected override IStreamParser<T> Parser => _parser;

        protected override void OnParsed(T content)
        {
            if (content == null) return;

            Interlocked.Exchange(ref _lastParsedUtcTicks, DateTime.UtcNow.Ticks);

            // 步骤1：先分发到解析报文流通道。
            // 为什么：提供通用异步消费入口，便于各协议读取解析结果。
            // 风险点：消费端跟不上时会触发通道丢弃，需监控丢包计数。
            if (!_parsedPacketChannel.Writer.TryWrite(content))
            {
                var dropped = Interlocked.Increment(ref _parsedPacketDropCount);
                if (ShouldSample(dropped))
                {
                    Logger.AddLog(LogLevel.Warning, string.Concat("[", _handlerName, "] Parsed packet channel dropped count: ", dropped));
                }
            }

            // 步骤2：无活跃请求时按策略丢弃响应。
            // 为什么：请求-响应模型下，空闲数据通常是噪声或无效上报。
            // 风险点：若不丢弃，响应通道会被无关数据挤占。
            if (_options.DropWhenNoActiveRequest && Volatile.Read(ref _activeRequests) <= 0)
            {
                var dropped = Interlocked.Increment(ref _idleDroppedCount);
                if (ShouldSample(dropped))
                {
                    Logger.AddLog(LogLevel.Warning, string.Concat("[", _handlerName, "] Dropped idle packets: ", dropped));
                }
                return;
            }

            // 步骤3：尝试入队到响应通道。
            // 为什么：后续匹配逻辑统一从通道读取。
            // 风险点：入队失败若不统计，会掩盖背压瓶颈。
            if (!TryEnqueueResponse(content))
            {
                var overflow = Interlocked.Read(ref _overflowDroppedCount);
                if (ShouldSample(overflow))
                {
                    Logger.AddLog(LogLevel.Warning, string.Concat("[", _handlerName, "] Dropped overflow packets: ", overflow));
                }
            }
        }

        protected virtual bool IsResponseMatch(T response, byte[] command)
            => _matcher?.IsResponseMatch(response, command) ?? true;

        protected virtual bool IsReportPacket(T response)
            => _matcher?.IsReportPacket(response) ?? false;

        protected virtual void OnReportPacket(T response)
            => _matcher?.OnReportPacket(response);

        protected virtual string BuildUnmatchedLog(T response)
            => _matcher?.BuildUnmatchedLog(response) ?? response.ToString() ?? typeof(T).Name;

        /// <summary>
        /// 获取当前指标快照。
        /// 可用于看板展示、健康检查或告警计算。
        /// </summary>
        public GenericHandlerMetrics GetMetrics()
            => new GenericHandlerMetrics(
                Interlocked.Read(ref _idleDroppedCount),
                Interlocked.Read(ref _overflowDroppedCount),
                Interlocked.Read(ref _unmatchedCount),
                Interlocked.Read(ref _retryCount),
                Interlocked.Read(ref _timeoutCount),
                Interlocked.Read(ref _matchedCount),
                Interlocked.Read(ref _totalLatencyMs),
                Volatile.Read(ref _activeRequests),
                Interlocked.Read(ref _waitModeQueueLength),
                Interlocked.Read(ref _waitModeQueueHighWatermark));

        public long GetParsedPacketDropCount()
            => Interlocked.Read(ref _parsedPacketDropCount);

        /// <summary>
        /// 读取解析完成的业务报文流。
        /// 解析线程产出完整对象后写入通道，业务线程可独立异步消费。
        /// </summary>
        public virtual IAsyncEnumerable<T> ReadParsedPacketsAsync(CancellationToken cancellationToken = default)
            => _parsedPacketChannel.Reader.ReadAllAsync(cancellationToken);

        /// <summary>
        /// 发送请求并等待匹配响应（通用核心流程）。
        /// </summary>
        /// <param name="command">待发送原始报文</param>
        /// <param name="timeout">单次等待超时时间（毫秒）</param>
        /// <param name="retryCount">超时后的重试次数</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        /// <returns>匹配成功的响应对象</returns>
        protected virtual async Task<T> SendRequestCoreAsync(byte[] command, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default)
        {
            if (command == null || command.Length < 1)
                throw new ArgumentException("Invalid command");

            // 步骤1：串行化请求发送与匹配流程。
            // 为什么：多数串口设备同一时刻只允许单请求在途。
            // 风险点：并行请求会导致响应串台与匹配错乱。
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _activeRequests);

                // 步骤2：按重试上限循环发送请求。
                // 为什么：临时抖动场景可通过重试提升成功率。
                // 风险点：重试过多会放大链路拥塞和超时累积。
                for (int i = 0; i <= retryCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attemptWatch = Stopwatch.StartNew();
                    await this.Send(command).ConfigureAwait(false);

                    // 步骤3：在超时窗口内持续读取并筛选响应。
                    // 为什么：同一窗口可能收到主动上报或非匹配包。
                    // 风险点：若不筛选，可能把错误响应返回给调用方。
                    while (true)
                    {
                        if (!_responseChannel.Reader.TryRead(out var result))
                        {
                            var remainingMs = timeout - (int)attemptWatch.ElapsedMilliseconds;
                            if (remainingMs <= 0)
                            {
                                if (i < retryCount)
                                {
                                    Interlocked.Increment(ref _retryCount);
                                    Logger.AddLog(LogLevel.Warning, string.Format("[{0}] Retry {1}/{2}...", _handlerName, i + 1, retryCount));
                                    break;
                                }

                                Interlocked.Increment(ref _timeoutCount);
                                await TryAutoRecoverAfterTimeoutAsync(cancellationToken).ConfigureAwait(false);
                                TryLogTimeoutRateAlert();
                                Logger.AddLog(LogLevel.Warning, BuildTimeoutDiagnostic(command, i + 1, retryCount + 1, timeout));
                                throw new TimeoutException($"Request timeout after {retryCount + 1} attempts");
                            }

                            var canRead = await WaitToReadAsyncWithTimeout(_responseChannel.Reader, remainingMs, cancellationToken).ConfigureAwait(false);
                            if (!canRead)
                            {
                                if (i < retryCount)
                                {
                                    Interlocked.Increment(ref _retryCount);
                                    Logger.AddLog(LogLevel.Warning, string.Format("[{0}] Retry {1}/{2}...", _handlerName, i + 1, retryCount));
                                    break;
                                }

                                Interlocked.Increment(ref _timeoutCount);
                                await TryAutoRecoverAfterTimeoutAsync(cancellationToken).ConfigureAwait(false);
                                TryLogTimeoutRateAlert();
                                Logger.AddLog(LogLevel.Warning, BuildTimeoutDiagnostic(command, i + 1, retryCount + 1, timeout));
                                throw new TimeoutException($"Request timeout after {retryCount + 1} attempts");
                            }

                            continue;
                        }

                        if (IsReportPacket(result))
                        {
                            try
                            {
                                OnReportPacket(result);
                            }
                            catch (Exception ex)
                            {
                                Logger.AddLog(LogLevel.Error, string.Concat("[", _handlerName, "] Report packet handling failed: ", ex.Message), exception: ex);
                            }
                            continue;
                        }

                        if (IsResponseMatch(result, command))
                        {
                            attemptWatch.Stop();
                            Interlocked.Increment(ref _matchedCount);
                            Interlocked.Add(ref _totalLatencyMs, attemptWatch.ElapsedMilliseconds);
                            Interlocked.Exchange(ref _consecutiveTimeoutCount, 0);
                            return result;
                        }

                        var unmatched = Interlocked.Increment(ref _unmatchedCount);
                        if (ShouldSample(unmatched))
                        {
                            Logger.AddLog(LogLevel.Warning, string.Concat("[", _handlerName, "] Skipped unmatched packets: ", unmatched, ", Last: ", BuildUnmatchedLog(result)));
                        }
                        // 诊断：解析成功但未匹配计数（unmatched）已自增并在采样时记录
                        // 为什么：用于判断响应是否到达但未与请求配对
                        // 风险点：过度输出会产生噪声，已通过 ShouldSample 控制
                    }
                }

                throw new TimeoutException("Should not happen");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
                _semaphore.Release();
            }
        }

        private static async Task<bool> WaitToReadAsyncWithTimeout(ChannelReader<T> reader, int timeoutMs, CancellationToken cancellationToken)
        {
            if (timeoutMs <= 0)
            {
                return reader.TryPeek(out _);
            }

            if (reader.TryPeek(out _))
            {
                return true;
            }

            try
            {
                return await reader
                    .WaitToReadAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// 判断当前计数是否命中采样日志输出点。
        /// </summary>
        protected bool ShouldSample(long count)
        {
            var interval = _options.SampleLogInterval;
            return interval > 0 && count % interval == 0;
        }

        /// <summary>
        /// 尝试把响应写入内部通道。
        /// 在不同满载策略下会采取不同处理方式。
        /// </summary>
        /// <returns>写入成功返回 true，否则 false</returns>
        protected bool TryEnqueueResponse(T content)
        {
            // 步骤1：优先直写主响应通道。
            // 为什么：减少额外排队延迟。
            // 风险点：主通道满载时需转入背压策略。
            if (_responseChannel.Writer.TryWrite(content))
            {
                return true;
            }

            if (_options.ResponseChannelFullMode == BoundedChannelFullMode.Wait)
            {
                // 步骤2：Wait 模式下写入临时背压队列。
                // 为什么：避免主解析线程长期阻塞。
                // 风险点：背压队列满后仍会丢包，需配合告警观测。
                if (_waitModeQueue != null && _waitModeQueue.Writer.TryWrite(content))
                {
                    var backlog = Interlocked.Increment(ref _waitModeQueueLength);
                    UpdateHighWatermark(backlog);
                    TryLogWaitBacklogAlert(backlog);

                    if (ShouldSample(backlog))
                    {
                        Logger.AddLog(LogLevel.Warning, string.Concat("[", _handlerName, "] Wait backlog=", backlog, ", high=", Interlocked.Read(ref _waitModeQueueHighWatermark)));
                    }

                    return true;
                }

                // 步骤3：背压队列也满时计入溢出丢弃。
                // 为什么：明确记录在极端负载下的数据损失。
                // 风险点：若只静默丢弃，压测结论会被误导。
                Interlocked.Increment(ref _overflowDroppedCount);
                return false;
            }

            Interlocked.Increment(ref _overflowDroppedCount);
            return false;
        }

        private void UpdateHighWatermark(long current)
        {
            while (true)
            {
                var observed = Interlocked.Read(ref _waitModeQueueHighWatermark);
                if (current <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _waitModeQueueHighWatermark, current, observed) == observed)
                {
                    return;
                }
            }
        }

        private void TryLogTimeoutRateAlert()
        {
            var threshold = _options.TimeoutRateAlertThresholdPercent;
            var minSamples = _options.TimeoutRateAlertMinSamples;
            if (threshold <= 0 || minSamples <= 0)
            {
                return;
            }

            var timeouts = Interlocked.Read(ref _timeoutCount);
            var matched = Interlocked.Read(ref _matchedCount);
            var total = timeouts + matched;
            if (total < minSamples || total % minSamples != 0)
            {
                return;
            }

            var timeoutRatePercent = (double)timeouts * 100d / total;
            if (timeoutRatePercent >= threshold)
            {
                Logger.AddLog(LogLevel.Error, $"[{_handlerName}] Timeout rate alert: {timeoutRatePercent:F2}% (timeouts={timeouts}, matched={matched}, threshold={threshold}%)");
            }
        }

        private void TryLogWaitBacklogAlert(long backlog)
        {
            var threshold = _options.WaitBacklogAlertThreshold;
            if (threshold <= 0 || backlog < threshold)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _waitBacklogAlertActive, 1, 0) == 0)
            {
                Logger.AddLog(LogLevel.Error, $"[{_handlerName}] Wait backlog alert: backlog={backlog}, threshold={threshold}");
            }
        }

        private string BuildTimeoutDiagnostic(byte[] command, int attempt, int totalAttempts, int timeoutMs)
        {
            var matched = Interlocked.Read(ref _matchedCount);
            var unmatched = Interlocked.Read(ref _unmatchedCount);
            var idleDropped = Interlocked.Read(ref _idleDroppedCount);
            var overflowDropped = Interlocked.Read(ref _overflowDroppedCount);
            var queueBacklog = Interlocked.Read(ref _waitModeQueueLength);
            var activeRequests = Volatile.Read(ref _activeRequests);
            var lastParsedTicks = Interlocked.Read(ref _lastParsedUtcTicks);
            var sinceLastParsedMs = lastParsedTicks > 0
                ? (long)(DateTime.UtcNow - new DateTime(lastParsedTicks, DateTimeKind.Utc)).TotalMilliseconds
                : -1;

            return $"[{_handlerName}] Timeout diagnostic: cmd={BitConverter.ToString(command)}, attempt={attempt}/{totalAttempts}, timeoutMs={timeoutMs}, matched={matched}, unmatched={unmatched}, idleDropped={idleDropped}, overflowDropped={overflowDropped}, waitBacklog={queueBacklog}, activeRequests={activeRequests}, sinceLastParsedMs={sinceLastParsedMs}";
        }

        private async Task TryAutoRecoverAfterTimeoutAsync(CancellationToken cancellationToken)
        {
            var consecutive = Interlocked.Increment(ref _consecutiveTimeoutCount);
            if (consecutive < 3)
            {
                return;
            }

            Interlocked.Exchange(ref _consecutiveTimeoutCount, 0);
            Logger.AddLog(LogLevel.Warning, $"[{_handlerName}] Consecutive timeout threshold reached, try force reconnect.");
            await TryReconnectAsync(cancellationToken, "consecutive request timeout", forceReopen: true).ConfigureAwait(false);
        }
    }
}