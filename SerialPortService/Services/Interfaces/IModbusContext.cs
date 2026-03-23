using SerialPortService.Models;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// Modbus 协议上下文接口。
    /// 同时支持一问一答请求和持续采集两种消费模型。
    /// </summary>
    public interface IModbusContext : IProtocolContext<ModbusPacket>
    {
        new Task<ModbusPacket> SendRequestAsync(byte[] command, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default);
    }
}
