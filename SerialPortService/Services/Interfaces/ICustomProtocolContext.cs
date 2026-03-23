using SerialPortService.Models;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 自定义协议上下文接口。
    /// 同时支持高层命令调用与底层原始请求/持续采集。
    /// </summary>
    public interface ICustomProtocolContext : IProtocolContext<CustomFrame>
    {
        Task<CustomFrame> SendRequestAsync(byte command, byte[]? payload = null, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default);
    }
}
