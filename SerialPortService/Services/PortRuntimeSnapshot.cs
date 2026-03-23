namespace SerialPortService.Services
{
    /// <summary>
    /// 串口上下文运行时快照。
    /// </summary>
    public readonly record struct PortRuntimeSnapshot(
        string PortName,
        bool IsRunning,
        bool IsOpen,
        bool LastCloseSucceeded,
        PortCloseState CloseState,
        long LastCloseDurationMs,
        long RawBytesTotal,
        long RawReadChunkCount,
        long ParsedEventDropCount,
        long ReconnectCycles,
        long ReconnectExhaustedCount,
        string? LastReconnectReason,
        long LastReconnectUtcTicks,
        IReadOnlyList<PortDiagnosticEvent> RecentEvents,
        IReadOnlyList<PortDiagnosticEvent> RecentErrors)
    {
        public HealthStatusLevel HealthStatus =>
            !LastCloseSucceeded || CloseState is PortCloseState.Faulted or PortCloseState.TimedOut || RecentErrors.Count > 0
                ? HealthStatusLevel.Faulted
                : ReconnectExhaustedCount > 0 || ParsedEventDropCount > 0
                    ? HealthStatusLevel.Warning
                    : HealthStatusLevel.Healthy;

        public long LastReconnectAgeMs =>
            LastReconnectUtcTicks <= 0
                ? -1
                : (long)(System.DateTime.UtcNow - new System.DateTime(LastReconnectUtcTicks, System.DateTimeKind.Utc)).TotalMilliseconds;
    }
}
