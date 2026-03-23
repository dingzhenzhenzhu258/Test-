namespace SerialPortService.Options
{
    /// <summary>
    /// 主动请求型协议的默认请求参数。
    /// 用于统一管理默认超时和重试次数，避免业务层分散硬编码。
    /// </summary>
    public sealed class RequestDefaultsOptions
    {
        /// <summary>
        /// 默认请求超时（毫秒）。
        /// </summary>
        public int TimeoutMs { get; init; } = 1000;

        /// <summary>
        /// 默认重试次数。
        /// </summary>
        public int RetryCount { get; init; } = 3;
    }
}
