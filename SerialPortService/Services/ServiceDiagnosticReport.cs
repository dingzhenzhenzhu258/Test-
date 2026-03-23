namespace SerialPortService.Services
{
    public readonly record struct ServiceDiagnosticReport(
        HealthStatusLevel HealthStatus,
        int OpenPortCount,
        int RunningPortCount,
        int FaultedPortCount,
        IReadOnlyList<PortDiagnosticEvent> RecentErrors,
        IReadOnlyList<PortDiagnosticEvent> RecentEvents);
}
