using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 温湿度传感器处理器
    /// </summary>
using Microsoft.Extensions.Logging;
// ...
    public class TemperatureSensorHandler : ModbusHandler
    {
        // ...
        public TemperatureSensorHandler(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            IStreamParser<ModbusPacket> parser,
            ILogger logger) // 保持签名兼容工厂调用
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
            // 如果传入的 parser 不是 ModbusRtuParser，这里可能会有行为不一致
            // 但目前架构下，TemperatureSensor 总是用 ModbusRTU
        }
    }
}
