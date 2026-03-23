namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 协议会话统一抽象。
    /// 同时暴露请求/响应与持续采集两种能力，便于协议级复用。
    /// </summary>
    public interface IProtocolContext<TPacket> : IRequestResponseContext<TPacket>, IParsedPacketSource<TPacket> where TPacket : class
    {
    }
}
