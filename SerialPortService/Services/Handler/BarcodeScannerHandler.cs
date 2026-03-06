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
    /// 扫码枪数据处理器
    /// </summary>
using Microsoft.Extensions.Logging;
// ...
    public class BarcodeScannerHandler : PortContext<string>
    {
        private readonly BarcodeParser _parser = new BarcodeParser();

        public BarcodeScannerHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
        }
// ...

        protected override IStreamParser<string> Parser => _parser;
    }
}
