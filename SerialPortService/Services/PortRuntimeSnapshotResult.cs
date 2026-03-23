namespace SerialPortService.Services
{
    public readonly record struct PortRuntimeSnapshotResult(
        bool IsSuccess,
        string Message,
        PortRuntimeSnapshot? Snapshot = null);
}

