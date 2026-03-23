using System.Collections.Generic;
using System.Threading;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 持续采集模型抽象。
    /// 用于把“消费已解析报文流”的能力从具体协议上下文中抽离。
    /// </summary>
    public interface IParsedPacketSource<TPacket> where TPacket : class
    {
        IAsyncEnumerable<TPacket> ReadParsedPacketsAsync(CancellationToken cancellationToken = default);
    }
}
