namespace SerialPortService.Services
{
    public readonly record struct PortDiagnosticEvent(
        long UtcTicks,
        string Category,
        string Message);
}

