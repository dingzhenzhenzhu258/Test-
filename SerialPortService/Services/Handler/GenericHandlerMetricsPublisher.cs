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
        }

        /// <summary>
        /// 注册一个指标提供者实例。
        /// </summary>
        /// <returns>注册 ID，用于后续显式注销</returns>
        public static int Register(IGenericHandlerMetricsProvider provider)
        {
            var id = Interlocked.Increment(ref _providerIdSeed);
            Providers[id] = new WeakReference<IGenericHandlerMetricsProvider>(provider);
            return id;
        }

        /// <summary>
        /// 显式注销指标提供者。
        /// 建议在处理器释放路径调用，避免仅依赖 GC 回收。
        /// </summary>
        public static void Unregister(int registrationId)
        {
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

        private static List<Measurement<long>> ObserveLong(Func<GenericHandlerMetrics, long> selector)
        {
            var measurements = new List<Measurement<long>>();
            foreach (var item in Providers.ToArray())
            {
                if (!item.Value.TryGetTarget(out var provider))
                {
                    Providers.TryRemove(item.Key, out _);
                    continue;
                }

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
                if (!item.Value.TryGetTarget(out var provider))
                {
                    Providers.TryRemove(item.Key, out _);
                    continue;
                }

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
                if (!item.Value.TryGetTarget(out var provider))
                {
                    Providers.TryRemove(item.Key, out _);
                    continue;
                }

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
