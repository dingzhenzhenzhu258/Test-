using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using System.IO.Ports;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 温湿度传感器处理器。
    /// 当前仍复用 Modbus 请求/响应链路，但允许工厂传入具体 parser。
    /// </summary>
    public class TemperatureSensorHandler : ModbusHandler
    {
        public TemperatureSensorHandler(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            IStreamParser<ModbusPacket> parser,
            ILogger logger,
            GenericHandlerOptions? options = null)
            : base(portName, baudRate, parity, dataBits, stopBits, parser, logger, options)
        {
        }
    }
}
