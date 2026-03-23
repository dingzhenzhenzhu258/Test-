using Logger.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Custom.Commands;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 自定义协议客户端。
    /// 通过命令定义对象统一构帧、校验和解码。
    /// </summary>
    public sealed class CustomProtocolClient
    {
        private readonly ICustomProtocolContext _context;
        private readonly ProtocolCommandClient<CustomFrame> _commandClient;

        public CustomProtocolClient(ICustomProtocolContext context, ILogger? logger = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _commandClient = new ProtocolCommandClient<CustomFrame>(_context, logger ?? NullLogger.Instance, static frame => frame.Raw, "Custom");
        }

        public async Task<TResult> ExecuteAsync<TResult>(
            ICustomProtocolCommand<TResult> command,
            int timeout = 1000,
            int retryCount = 3,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            return await _commandClient
                .ExecuteAsync(command, timeout, retryCount, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
