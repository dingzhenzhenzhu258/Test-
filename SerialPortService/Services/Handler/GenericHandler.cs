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
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private int _activeRequests;
        private long _idleDroppedCount;
        private long _overflowDroppedCount;
        private long _unmatchedCount;
        private long _retryCount;
        private long _timeoutCount;
        private long _matchedCount;
        private long _totalLatencyMs;
        private readonly int _metricsRegistrationId;
        private int _disposeSignaled;

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

            _responseChannel = Channel.CreateBounded<T>(new BoundedChannelOptions(_options.ResponseChannelCapacity)
            {
                FullMode = _options.ResponseChannelFullMode,
                SingleReader = true,
                SingleWriter = true
            });

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
                GenericHandlerMetricsPublisher.Unregister(_metricsRegistrationId);
                GC.SuppressFinalize(this);
            }

            base.Dispose();
        }

        protected override IStreamParser<T> Parser => _parser;

        protected override void OnParsed(T content)
        {
            if (content == null) return;

            if (_options.DropWhenNoActiveRequest && Volatile.Read(ref _activeRequests) <= 0)
            {
                var dropped = Interlocked.Increment(ref _idleDroppedCount);
                if (ShouldSample(dropped))
                {
                    Logger.AddLog(LogLevel.Warning, $"[{_handlerName}] Dropped idle packets: {dropped}");
                }
                return;
            }

            if (!TryEnqueueResponse(content))
            {
                var overflow = Interlocked.Read(ref _overflowDroppedCount);
                if (ShouldSample(overflow))
                {
                    Logger.AddLog(LogLevel.Warning, $"[{_handlerName}] Dropped overflow packets: {overflow}");
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
                Volatile.Read(ref _activeRequests));

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

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _activeRequests);

                while (_responseChannel.Reader.TryRead(out _)) { }

                for (int i = 0; i <= retryCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attemptWatch = Stopwatch.StartNew();
                    await this.Send(command).ConfigureAwait(false);

                    using var timeoutCts = new CancellationTokenSource(timeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                    while (true)
                    {
                        T result;
                        try
                        {
                            result = await _responseChannel.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }

                            if (i < retryCount)
                            {
                                Interlocked.Increment(ref _retryCount);
                                Logger.AddLog(LogLevel.Warning, $"[{_handlerName}] Retry {i + 1}/{retryCount}...");
                                break;
                            }

                            Interlocked.Increment(ref _timeoutCount);
                            throw new TimeoutException($"Request timeout after {retryCount + 1} attempts");
                        }

                        if (IsReportPacket(result))
                        {
                            try
                            {
                                OnReportPacket(result);
                            }
                            catch (Exception ex)
                            {
                                Logger.AddLog(LogLevel.Error, $"[{_handlerName}] Report packet handling failed: {ex.Message}", exception: ex);
                            }
                            continue;
                        }

                        if (IsResponseMatch(result, command))
                        {
                            attemptWatch.Stop();
                            Interlocked.Increment(ref _matchedCount);
                            Interlocked.Add(ref _totalLatencyMs, attemptWatch.ElapsedMilliseconds);
                            return result;
                        }

                        var unmatched = Interlocked.Increment(ref _unmatchedCount);
                        if (ShouldSample(unmatched))
                        {
                            Logger.AddLog(LogLevel.Warning, $"[{_handlerName}] Skipped unmatched packets: {unmatched}, Last: {BuildUnmatchedLog(result)}");
                        }
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
            if (_responseChannel.Writer.TryWrite(content))
            {
                return true;
            }

            if (_options.ResponseChannelFullMode != BoundedChannelFullMode.Wait)
            {
                Interlocked.Increment(ref _overflowDroppedCount);
                return false;
            }

            if (_responseChannel.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _overflowDroppedCount);
                return _responseChannel.Writer.TryWrite(content);
            }

            Interlocked.Increment(ref _overflowDroppedCount);
            return false;
        }
    }
}