using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 指标提供者接口。
    /// 每个处理器实例通过该接口暴露当前指标快照与标签维度。
    /// </summary>
    internal interface IGenericHandlerMetricsProvider
    {
        /// <summary>
        /// 处理器名称标签（例如 ModbusHandler）。
        /// </summary>
        string HandlerName { get; }

        /// <summary>
        /// 串口名称标签（例如 COM3）。
        /// </summary>
        string PortName { get; }

        /// <summary>
        /// 协议名称标签（例如 ModbusRTU）。
        /// </summary>
        string ProtocolName { get; }

        /// <summary>
        /// 设备类型标签（例如 TemperatureSensor）。
        /// </summary>
        string DeviceType { get; }

        /// <summary>
        /// 获取当前处理器指标快照。
        /// </summary>
        GenericHandlerMetrics GetMetrics();
    }

    /// <summary>
    /// 通用处理器指标发布器。
    /// 通过 <see cref="System.Diagnostics.Metrics.Meter"/> 暴露 ObservableGauge，
    /// 由 OpenTelemetry 定期拉取并上报到指标后端（例如 OpenObserve）。
    /// </summary>
    internal static class GenericHandlerMetricsPublisher
    {
        /// <summary>
        /// Meter 名称，需在 OTel metrics 配置中显式 AddMeter。
        /// </summary>
        public const string MeterName = "SerialPortService.GenericHandler";

        private static readonly Meter Meter = new(MeterName);
        private static readonly ConcurrentDictionary<int, WeakReference<IGenericHandlerMetricsProvider>> Providers = new();
        private static int _providerIdSeed;

        static GenericHandlerMetricsPublisher()
        {
            Meter.CreateObservableGauge<long>("serialport.handler.idle_dropped", ObserveIdleDropped);
            Meter.CreateObservableGauge<long>("serialport.handler.overflow_dropped", ObserveOverflowDropped);
            Meter.CreateObservableGauge<long>("serialport.handler.unmatched", ObserveUnmatched);
            Meter.CreateObservableGauge<long>("serialport.handler.retry_count", ObserveRetryCount);
            Meter.CreateObservableGauge<long>("serialport.handler.timeout_count", ObserveTimeoutCount);
            Meter.CreateObservableGauge<long>("serialport.handler.matched_count", ObserveMatchedCount);
            Meter.CreateObservableGauge<double>("serialport.handler.avg_latency_ms", ObserveAvgLatency);
            Meter.CreateObservableGauge<int>("serialport.handler.active_requests", ObserveActiveRequests);
            Meter.CreateObservableGauge<long>("serialport.handler.wait_backlog", ObserveWaitBacklog);
            Meter.CreateObservableGauge<long>("serialport.handler.wait_backlog_high_watermark", ObserveWaitBacklogHighWatermark);
        }

        /// <summary>
        /// 注册一个指标提供者实例。
        /// </summary>
        /// <returns>注册 ID，用于后续显式注销</returns>
        public static int Register(IGenericHandlerMetricsProvider provider)
        {
            // 步骤1：生成递增注册 ID。
            // 为什么：用于后续显式注销与弱引用表索引。
            // 风险点：若 ID 重复，可能覆盖已有提供者引用。
            var id = Interlocked.Increment(ref _providerIdSeed);

            // 步骤2：保存弱引用。
            // 为什么：避免指标系统强引用导致处理器无法被 GC。
            // 风险点：若改用强引用，长时间运行会造成内存泄漏。
            Providers[id] = new WeakReference<IGenericHandlerMetricsProvider>(provider);
            return id;
        }

        /// <summary>
        /// 显式注销指标提供者。
        /// 建议在处理器释放路径调用，避免仅依赖 GC 回收。
        /// </summary>
        public static void Unregister(int registrationId)
        {
            // 步骤1：按注册 ID 清理提供者。
            // 为什么：释放已销毁处理器的指标入口。
            // 风险点：未注销会增加遍历成本并污染指标集。
            Providers.TryRemove(registrationId, out _);
        }

        private static List<Measurement<long>> ObserveIdleDropped() => ObserveLong(m => m.IdleDropped);
        private static List<Measurement<long>> ObserveOverflowDropped() => ObserveLong(m => m.OverflowDropped);
        private static List<Measurement<long>> ObserveUnmatched() => ObserveLong(m => m.Unmatched);
        private static List<Measurement<long>> ObserveRetryCount() => ObserveLong(m => m.RetryCount);
        private static List<Measurement<long>> ObserveTimeoutCount() => ObserveLong(m => m.TimeoutCount);
        private static List<Measurement<long>> ObserveMatchedCount() => ObserveLong(m => m.MatchedCount);
        private static List<Measurement<double>> ObserveAvgLatency() => ObserveDouble(m => m.AverageLatencyMs);
        private static List<Measurement<int>> ObserveActiveRequests() => ObserveInt(m => m.ActiveRequests);
        private static List<Measurement<long>> ObserveWaitBacklog() => ObserveLong(m => m.WaitBacklog);
        private static List<Measurement<long>> ObserveWaitBacklogHighWatermark() => ObserveLong(m => m.WaitBacklogHighWatermark);

        private static List<Measurement<long>> ObserveLong(Func<GenericHandlerMetrics, long> selector)
        {
            var measurements = new List<Measurement<long>>();
            foreach (var item in Providers.ToArray())
            {
                // 步骤1：清理已失效弱引用。
                // 为什么：避免对已释放处理器继续采样。
                // 风险点：失效引用不清理会导致字典持续膨胀。
                if (!item.Value.TryGetTarget(out var provider))
                {
                    Providers.TryRemove(item.Key, out _);
                    continue;
                }

                // 步骤2：采样当前指标并附加标签。
                // 为什么：保证指标上报含有端口/协议等维度信息。
                // 风险点：标签缺失会降低故障定位效率。
                var metrics = provider.GetMetrics();
                measurements.Add(new Measurement<long>(selector(metrics), CreateTags(provider)));
            }
            return measurements;
        }

        private static List<Measurement<double>> ObserveDouble(Func<GenericHandlerMetrics, double> selector)
        {
            var measurements = new List<Measurement<double>>();
            foreach (var item in Providers.ToArray())
            {
                // 步骤1：清理已失效弱引用。
                // 为什么：避免无效采样开销。
                // 风险点：长期不清理会拉高采样延迟。
                if (!item.Value.TryGetTarget(out var provider))
                {
                    Providers.TryRemove(item.Key, out _);
                    continue;
                }

                // 步骤2：采样双精度指标并附加标签。
                // 为什么：如平均延迟等指标需要浮点精度。
                // 风险点：若误用整型会造成精度丢失。
                var metrics = provider.GetMetrics();
                measurements.Add(new Measurement<double>(selector(metrics), CreateTags(provider)));
            }
            return measurements;
        }

        private static List<Measurement<int>> ObserveInt(Func<GenericHandlerMetrics, int> selector)
        {
            var measurements = new List<Measurement<int>>();
            foreach (var item in Providers.ToArray())
            {
                // 步骤1：清理已失效弱引用。
                // 为什么：维持提供者集合健康状态。
                // 风险点：无效项过多会拖慢每次 ObservableGauge 回调。
                if (!item.Value.TryGetTarget(out var provider))
                {
                    Providers.TryRemove(item.Key, out _);
                    continue;
                }

                // 步骤2：采样整型指标并附加标签。
                // 为什么：活跃请求数等计数指标适合整型表示。
                // 风险点：标签异常会导致多实例指标聚合错误。
                var metrics = provider.GetMetrics();
                measurements.Add(new Measurement<int>(selector(metrics), CreateTags(provider)));
            }
            return measurements;
        }

        /// <summary>
        /// 构建指标标签。
        /// </summary>
        private static KeyValuePair<string, object?>[] CreateTags(IGenericHandlerMetricsProvider provider)
            =>
            [
                new("handler", provider.HandlerName),
                new("port", provider.PortName),
                new("protocol", provider.ProtocolName),
                new("deviceType", provider.DeviceType)
            ];
    }
}
