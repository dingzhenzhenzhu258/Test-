namespace SerialPortService.Services
{
    /// <summary>
    /// 串口重连策略全局配置。
    /// 由 <see cref="SerialPortServiceBase"/> 在初始化时统一写入，
    /// 由 <see cref="PortContext{T}"/> 在读写异常路径读取执行。
    /// </summary>
    internal static class SerialPortReconnectPolicy
    {
        private static int _reconnectIntervalMs = 1000;
        private static int _maxReconnectAttempts = 3;

        /// <summary>
        /// 重连间隔（毫秒）。
        /// </summary>
        public static int ReconnectIntervalMs => Volatile.Read(ref _reconnectIntervalMs);

        /// <summary>
        /// 最大重连尝试次数。
        /// </summary>
        public static int MaxReconnectAttempts => Volatile.Read(ref _maxReconnectAttempts);

        /// <summary>
        /// 更新重连策略。
        /// 仅当输入值大于 0 时生效。
        /// </summary>
        public static void Configure(int reconnectIntervalMs, int maxReconnectAttempts)
        {
            if (reconnectIntervalMs > 0)
            {
                Volatile.Write(ref _reconnectIntervalMs, reconnectIntervalMs);
            }

            if (maxReconnectAttempts > 0)
            {
                Volatile.Write(ref _maxReconnectAttempts, maxReconnectAttempts);
            }
        }
    }
}
