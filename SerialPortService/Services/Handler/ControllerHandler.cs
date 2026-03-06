using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Parser;
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
using Microsoft.Extensions.Logging;
// ...
    public class ControllerHandler : PortContext<string>
    {
        // 实例化解析器
        private readonly ControllerParser _parser = new ControllerParser();

        public ControllerHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
        }
// ...

        // 将解析器通过属性暴露给基类
        protected override IStreamParser<string> Parser => _parser;
    }
}
