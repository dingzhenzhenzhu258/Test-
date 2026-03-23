namespace SerialPortService.Services
{
    public readonly record struct ProtocolDefinitionRegistrationResult(
        bool IsSuccess,
        string Message,
        string? Key = null,
        string? ExistingKey = null);
}

