using SerialPortService.Models;
using SerialPortService.Services.Parser;
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
    /// 扫码枪数据处理器
    /// </summary>
    public class BarcodeScannerHandler : ParserPortContext<string>
    {
        /// <summary>
        /// 创建扫码枪处理器。
        /// </summary>
        public BarcodeScannerHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, new BarcodeParser(), logger)
        {
            // 步骤1：沿用基类初始化串口管线。
            // 为什么：扫码场景无需额外构造逻辑。
            // 风险点：若后续扩展初始化，这里需要同步补充异常处理。
        }
    }
}
