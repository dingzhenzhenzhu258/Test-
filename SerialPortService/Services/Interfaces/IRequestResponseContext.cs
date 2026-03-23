using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 请求/响应模型抽象。
    /// 用于把“发送原始请求并等待匹配响应”的能力从具体协议上下文中抽离。
    /// </summary>
    public interface IRequestResponseContext<TPacket> where TPacket : class
    {
        Task<TPacket> SendRequestAsync(byte[] request, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default);
    }
}
