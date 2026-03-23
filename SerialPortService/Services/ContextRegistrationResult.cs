namespace SerialPortService.Services
{
    /// <summary>
    /// 上下文注册结果。
    /// </summary>
    public readonly record struct ContextRegistrationResult(
        bool IsSuccess,
        string Message,
        string? Key = null,
        string? ExistingKey = null);
}
