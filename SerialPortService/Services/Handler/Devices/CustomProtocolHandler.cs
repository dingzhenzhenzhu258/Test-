using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Parser;
using SerialPortService.Services.Protocols.Custom;
using SerialPortService.Services.Protocols.Custom.Commands;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 自定义协议处理器示例。
    /// 复用 GenericHandler 的高并发、限流、重试、超时、指标与重连能力。
    /// </summary>
    public class CustomProtocolHandler : GenericHandler<CustomFrame>, ICustomProtocolContext, IRequestResponseContext<CustomFrame>
    {
        private static readonly CustomProtocolDefinition s_protocolDefinition = new();

        public CustomProtocolHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger, GenericHandlerOptions? options = null)
            : base(portName, baudRate, parity, dataBits, stopBits, s_protocolDefinition.CreateParser(), logger, options, s_protocolDefinition.CreateResponseMatcher())
        {
        }

        public async Task<CustomFrame> SendRequestAsync(byte command, byte[]? payload = null, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default)
        {
            var request = CustomProtocolFrameBuilder.Build(command, payload ?? Array.Empty<byte>());
            return await SendRequestCoreAsync(request, timeout, retryCount, cancellationToken).ConfigureAwait(false);
        }

        async Task<CustomFrame> IRequestResponseContext<CustomFrame>.SendRequestAsync(byte[] request, int timeout, int retryCount, CancellationToken cancellationToken)
            => await SendRequestCoreAsync(request, timeout, retryCount, cancellationToken).ConfigureAwait(false);

        public async Task<TResult> ExecuteCommandAsync<TResult>(ICustomProtocolCommand<TResult> command, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            var response = await SendRequestAsync(command.CommandByte, command.PayloadBytes, timeout, retryCount, cancellationToken).ConfigureAwait(false);
            command.ValidateResponse(response);
            return command.DecodeResponse(response);
        }

        public override IAsyncEnumerable<CustomFrame> ReadParsedPacketsAsync(CancellationToken cancellationToken = default)
            => base.ReadParsedPacketsAsync(cancellationToken);
    }
}
