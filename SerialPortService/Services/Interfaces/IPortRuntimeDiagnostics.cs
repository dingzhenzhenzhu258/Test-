namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 运行时诊断快照接口。
    /// 用于在不侵入业务发送/接收 API 的前提下读取端口长期运行状态。
    /// </summary>
    public interface IPortRuntimeDiagnostics
    {
        PortRuntimeSnapshot GetRuntimeSnapshot();

        IReadOnlyList<PortDiagnosticEvent> GetRecentEvents();

        IReadOnlyList<PortDiagnosticEvent> GetRecentErrors();
    }
}
