using SerialPortService.Services.Parser;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 自定义协议上下文接口（示例）。
    /// 该接口定义“请求-响应式”协议调用入口，
    /// 便于业务层不依赖具体 Handler 类型。
    /// </summary>
    public interface ICustomProtocolContext
    {
        /// <summary>
        /// 发送一条自定义协议请求并等待匹配响应。
        /// </summary>
        /// <param name="command">协议命令字</param>
        /// <param name="payload">协议负载，可为空</param>
        /// <param name="timeout">单次请求超时时间（毫秒）</param>
        /// <param name="retryCount">超时后的重试次数</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        /// <returns>解析后的响应帧</returns>
        Task<CustomFrame> SendRequestAsync(byte command, byte[]? payload = null, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default);
    }
}
