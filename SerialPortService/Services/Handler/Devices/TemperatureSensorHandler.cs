using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using Microsoft.Extensions.Logging;
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
    public class TemperatureSensorHandler : ModbusHandler
    {
        /// <summary>
        /// 创建温湿度传感器处理器。
        /// </summary>
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
            // 步骤1：保持构造签名与工厂兼容。
            // 为什么：历史调用路径会传入 parser 参数。
            // 风险点：直接移除参数会破坏现有调用方兼容性。

            // 步骤2：当前实现统一复用 ModbusHandler 解析链路。
            // 为什么：温湿度设备当前协议固定为 ModbusRTU。
            // 风险点：若未来引入非 RTU 解析器，需要在此处显式分支处理。
        }
    }
}
