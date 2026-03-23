using Microsoft.Extensions.Logging;
using SerialPortService.Models.Enums;
using System.IO.Ports;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 串口上下文注册项。
    /// 用于将“设备/协议 -> 上下文构造逻辑”从中心工厂中解耦出来。
    /// </summary>
    public interface IPortContextRegistration
    {
        bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol);

        IPortContext Create(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            HandleEnum handleEnum,
            ProtocolEnum protocol,
            ILoggerFactory loggerFactory,
            Services.Handler.GenericHandlerOptions options);
    }
}
