using System.Linq;

namespace SerialPortService.Services
{
    public readonly record struct ServiceHealthSnapshot(
        int OpenPortCount,
        int RunningPortCount,
        int FaultedPortCount,
        IReadOnlyList<PortRuntimeSnapshot> Ports)
    {
        public HealthStatusLevel HealthStatus =>
            FaultedPortCount > 0
                ? HealthStatusLevel.Faulted
                : Ports.Any(x => x.HealthStatus == HealthStatusLevel.Warning)
                    ? HealthStatusLevel.Warning
                    : HealthStatusLevel.Healthy;
    }
}
