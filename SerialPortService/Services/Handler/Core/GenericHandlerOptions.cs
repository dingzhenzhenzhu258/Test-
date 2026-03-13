using System.Threading.Channels;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// <see cref="GenericHandler{T}"/> 运行参数。
    /// 该配置用于控制响应通道容量、日志采样、空闲丢弃策略与重连策略。
    /// 建议通过 <c>appsettings.json</c> 下发，避免现场硬编码。
    /// </summary>
    public sealed class GenericHandlerOptions
    {
        /// <summary>
        /// 响应通道容量。
        /// 容量越大越能抗突发，但会增加内存占用。
        /// </summary>
        public int ResponseChannelCapacity { get; init; } = 512;

        /// <summary>
        /// 采样日志间隔。
        /// 例如为 200 时，每 200 次事件记录 1 条日志，避免日志风暴。
        /// </summary>
        public int SampleLogInterval { get; init; } = 200;

        /// <summary>
        /// 当没有活跃请求时是否丢弃解析结果。
        /// 请求-响应型协议建议开启，以降低无效积压。
        /// </summary>
        public bool DropWhenNoActiveRequest { get; init; } = true;

        /// <summary>
        /// 响应通道满载行为。
        /// 常用值：<see cref="BoundedChannelFullMode.Wait"/>、<see cref="BoundedChannelFullMode.DropOldest"/>。
        /// </summary>
        public BoundedChannelFullMode ResponseChannelFullMode { get; init; } = BoundedChannelFullMode.Wait;

        /// <summary>
        /// Wait 模式下的背压队列容量。
        /// 当响应通道暂时不可写时，解析线程会先写入该队列，超过容量后按丢弃处理。
        /// </summary>
        public int WaitModeQueueCapacity { get; init; } = 4096;

        /// <summary>
        /// 指标标签：协议名称。
        /// 若为空，会自动回退到解析器类型名。
        /// </summary>
        public string? ProtocolTag { get; init; }

        /// <summary>
        /// 指标标签：设备类型。
        /// 若为空，会自动回退到处理器类型名。
        /// </summary>
        public string? DeviceTypeTag { get; init; }

        /// <summary>
        /// 断线重连间隔（毫秒）。
        /// </summary>
        public int ReconnectIntervalMs { get; init; } = 1000;

        /// <summary>
        /// 单次异常场景下的最大重连尝试次数。
        /// </summary>
        public int MaxReconnectAttempts { get; init; } = 3;

        /// <summary>
        /// 超时率告警阈值（百分比，0-100）。
        /// 当 timeout/(timeout+matched) 超过该值时触发告警。
        /// 设为 0 表示关闭。
        /// </summary>
        public int TimeoutRateAlertThresholdPercent { get; init; } = 20;

        /// <summary>
        /// 超时率告警最小样本数。
        /// </summary>
        public int TimeoutRateAlertMinSamples { get; init; } = 20;

        /// <summary>
        /// Wait 模式队列积压告警阈值。
        /// 当 WaitBacklog 达到该值时触发告警。
        /// 设为 0 表示关闭。
        /// </summary>
        public int WaitBacklogAlertThreshold { get; init; } = 1024;

        /// <summary>
        /// 重连失败率告警阈值（百分比，0-100）。
        /// </summary>
        public int ReconnectFailureRateAlertThresholdPercent { get; init; } = 30;

        /// <summary>
        /// 重连失败率告警最小样本数。
        /// </summary>
        public int ReconnectFailureRateAlertMinSamples { get; init; } = 20;
    }
}
