using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
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
    public class BarcodeScannerHandler : PortContext<string>
    {
        // 步骤1：持有扫码解析器实例。
        // 为什么：把逐字节串流转换为完整扫码字符串。
        // 风险点：解析器状态污染会导致扫码结果串包。
        private readonly BarcodeParser _parser = new BarcodeParser();

        public BarcodeScannerHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
            // 步骤1：沿用基类初始化串口管线。
            // 为什么：扫码场景无需额外构造逻辑。
            // 风险点：若后续扩展初始化，这里需要同步补充异常处理。
        }

        // 步骤1：向基类暴露解析器。
        // 为什么：基类解析循环通过该属性驱动解析。
        // 风险点：返回错误解析器会导致数据语义错乱。
        protected override IStreamParser<string> Parser => _parser;
    }
}
