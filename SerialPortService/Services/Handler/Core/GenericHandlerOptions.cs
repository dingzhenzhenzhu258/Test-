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
        /// 基础发送通道容量。
        /// 适用于所有 <see cref="PortContext{T}"/>，用于限制待发送命令积压。
        /// </summary>
        public int SendChannelCapacity { get; init; } = 512;

        /// <summary>
        /// 原始输入块通道容量。
        /// 读取线程与解析线程之间通过该通道解耦，过小会放大读取等待，过大会增加驻留内存。
        /// </summary>
        public int RawInputChannelCapacity { get; init; } = 500;

        /// <summary>
        /// 单次从串口底层流读取的块大小（字节）。
        /// </summary>
        public int RawReadBufferSize { get; init; } = 4096;

        /// <summary>
        /// 串口驱动层读缓冲区大小（字节）。
        /// </summary>
        public int SerialPortReadBufferSize { get; init; } = 1024 * 1024;

        /// <summary>
        /// 是否输出每个原始读块的十六进制日志。
        /// 高频采集默认应关闭，否则会明显放大 CPU、字符串分配和磁盘 IO。
        /// </summary>
        public bool EnableRawReadChunkLog { get; init; } = false;

        /// <summary>
        /// 原始字节统计日志间隔（秒）。
        /// 设为 0 或负数表示关闭定时统计日志。
        /// </summary>
        public int RawBytesLogIntervalSeconds { get; init; } = 60;

        /// <summary>
        /// 是否将 <c>OnHandleChanged</c> 事件从解析线程异步分发到独立通道。
        /// 高频采集默认建议开启，避免事件订阅方阻塞解析主路径。
        /// </summary>
        public bool DispatchParsedEventAsync { get; init; } = true;

        /// <summary>
        /// <c>OnHandleChanged</c> 事件分发通道容量。
        /// </summary>
        public int ParsedEventChannelCapacity { get; init; } = 1024;

        /// <summary>
        /// <c>OnHandleChanged</c> 事件分发通道满载策略。
        /// 默认使用 <see cref="BoundedChannelFullMode.DropOldest"/> 以优先保留最新事件。
        /// </summary>
        public BoundedChannelFullMode ParsedEventChannelFullMode { get; init; } = BoundedChannelFullMode.DropOldest;

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
