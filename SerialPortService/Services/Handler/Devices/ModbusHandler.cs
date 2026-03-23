using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Modbus;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// Modbus 处理器。
    /// 基于 GenericHandler 复用通用收发能力，协议特有匹配规则由协议定义提供。
    /// </summary>
    public class ModbusHandler : GenericHandler<ModbusPacket>, IModbusContext
    {
        private static readonly ModbusProtocolDefinition s_protocolDefinition = new();

        public ModbusHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger, GenericHandlerOptions? options = null)
            : this(portName, baudRate, parity, dataBits, stopBits, s_protocolDefinition.CreateParser(), logger, options)
        {
        }

        public ModbusHandler(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            IStreamParser<ModbusPacket> parser,
            ILogger logger,
            GenericHandlerOptions? options = null)
            : base(portName, baudRate, parity, dataBits, stopBits, parser ?? throw new ArgumentNullException(nameof(parser)), logger, options, s_protocolDefinition.CreateResponseMatcher())
        {
        }

        public async Task<ModbusPacket> SendRequestAsync(byte[] command, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default)
            => await SendRequestCoreAsync(command, timeout, retryCount, cancellationToken).ConfigureAwait(false);

        public override IAsyncEnumerable<ModbusPacket> ReadParsedPacketsAsync(CancellationToken cancellationToken = default)
            => base.ReadParsedPacketsAsync(cancellationToken);
    }
}
