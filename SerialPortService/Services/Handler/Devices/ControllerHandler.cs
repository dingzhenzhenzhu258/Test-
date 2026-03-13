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
    /// 控制器处理器
    /// </summary>
    public class ControllerHandler : ParserPortContext<string>
    {
        public ControllerHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, new ControllerParser(), logger)
        {
            // 步骤1：沿用基类初始化串口上下文。
            // 为什么：控制器处理器不需要额外构造参数。
            // 风险点：后续新增资源时需补充释放逻辑。
        }
    }
}
