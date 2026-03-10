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
    /// 控制器处理器
    /// </summary>
    public class ControllerHandler : PortContext<string>
    {
        // 步骤1：创建控制器解析器。
        // 为什么：将固定长度控制器报文转换为业务状态字符串。
        // 风险点：解析规则变化时若未同步更新，会产生状态误判。
        private readonly ControllerParser _parser = new ControllerParser();

        public ControllerHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
            // 步骤1：沿用基类初始化串口上下文。
            // 为什么：控制器处理器不需要额外构造参数。
            // 风险点：后续新增资源时需补充释放逻辑。
        }

        // 步骤1：向基类暴露解析器实例。
        // 为什么：基类解析循环通过该属性统一调用 TryParse。
        // 风险点：错误解析器会导致按键状态判断失真。
        protected override IStreamParser<string> Parser => _parser;
    }
}
