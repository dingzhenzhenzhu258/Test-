namespace SerialPortService.Services
{
    public readonly record struct ParserRegistrationResult(
        bool IsSuccess,
        string Message,
        string? Key = null,
        string? ExistingKey = null);
}
