using SerialPortService.Models;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// Modbus 协议上下文接口。
    /// 对业务层暴露“发送请求并等待匹配响应”的统一能力。
    /// </summary>
    public interface IModbusContext
    {
        /// <summary>
        /// 发送 Modbus 请求并等待匹配响应。
        /// </summary>
        /// <param name="command">完整 Modbus 帧（含从站、功能码、数据与 CRC）</param>
        /// <param name="timeout">单次请求超时时间（毫秒）</param>
        /// <param name="retryCount">超时后的重试次数</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        /// <returns>解析后的 Modbus 响应包</returns>
        Task<ModbusPacket> SendRequestAsync(byte[] command, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default);
    }
}
