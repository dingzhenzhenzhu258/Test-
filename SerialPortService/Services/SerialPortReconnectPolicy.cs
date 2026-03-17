namespace SerialPortService.Services
{
    /// <summary>
    /// 串口重连策略全局配置。
    /// 由 <see cref="SerialPortServiceBase"/> 在初始化时统一写入，
    /// 由 <see cref="PortContext{T}"/> 在读写异常路径读取执行。
    /// </summary>
    internal static class SerialPortReconnectPolicy
    {
        /// <summary>
        /// 当前重连间隔毫秒值。
        /// </summary>
        private static int _reconnectIntervalMs = 1000;

        /// <summary>
        /// 当前最大重连尝试次数。
        /// </summary>
        private static int _maxReconnectAttempts = 3;

        /// <summary>
        /// 当前重连失败率告警阈值百分比。
        /// </summary>
        private static int _reconnectFailureRateAlertThresholdPercent = 30;

        /// <summary>
        /// 当前重连失败率告警最小样本数。
        /// </summary>
        private static int _reconnectFailureRateAlertMinSamples = 20;

        /// <summary>
        /// 重连间隔（毫秒）。
        /// </summary>
        public static int ReconnectIntervalMs => Volatile.Read(ref _reconnectIntervalMs);

        /// <summary>
        /// 最大重连尝试次数。
        /// </summary>
        public static int MaxReconnectAttempts => Volatile.Read(ref _maxReconnectAttempts);

        /// <summary>
        /// 重连失败率告警阈值（百分比）。
        /// </summary>
        public static int ReconnectFailureRateAlertThresholdPercent => Volatile.Read(ref _reconnectFailureRateAlertThresholdPercent);

        /// <summary>
        /// 重连失败率告警最小样本数。
        /// </summary>
        public static int ReconnectFailureRateAlertMinSamples => Volatile.Read(ref _reconnectFailureRateAlertMinSamples);

        /// <summary>
        /// 更新重连策略。
        /// 时间/次数类参数仅当输入值大于 0 时生效；
        /// 失败率阈值支持 0~100（0 表示关闭该类告警）。
        /// </summary>
        /// <param name="reconnectIntervalMs">重连间隔（毫秒）</param>
        /// <param name="maxReconnectAttempts">最大重连次数</param>
        /// <param name="reconnectFailureRateAlertThresholdPercent">重连失败率告警阈值</param>
        /// <param name="reconnectFailureRateAlertMinSamples">重连失败率告警最小样本数</param>
        public static void Configure(int reconnectIntervalMs, int maxReconnectAttempts, int reconnectFailureRateAlertThresholdPercent, int reconnectFailureRateAlertMinSamples)
        {
            // 步骤1：更新重连间隔。
            // 为什么：为重连退避提供统一节奏。
            // 风险点：间隔过小会造成重连风暴，过大则恢复变慢。
            if (reconnectIntervalMs > 0)
            {
                Volatile.Write(ref _reconnectIntervalMs, reconnectIntervalMs);
            }

            // 步骤2：更新最大重连次数。
            // 为什么：限制单次故障的重试成本。
            // 风险点：次数过少易误判故障，过多会拖慢失败回退。
            if (maxReconnectAttempts > 0)
            {
                Volatile.Write(ref _maxReconnectAttempts, maxReconnectAttempts);
            }

            // 步骤3：更新重连失败率告警阈值。
            // 为什么：让告警策略可配置并适配不同现场噪声。
            // 风险点：阈值不合理会导致漏报或告警风暴。
            if (reconnectFailureRateAlertThresholdPercent is >= 0 and <= 100)
            {
                Volatile.Write(ref _reconnectFailureRateAlertThresholdPercent, reconnectFailureRateAlertThresholdPercent);
            }

            // 步骤4：更新失败率告警最小样本数。
            // 为什么：降低小样本波动造成的误告警。
            // 风险点：样本过小导致抖动，过大则告警滞后。
            if (reconnectFailureRateAlertMinSamples > 0)
            {
                Volatile.Write(ref _reconnectFailureRateAlertMinSamples, reconnectFailureRateAlertMinSamples);
            }
        }
    }
}
