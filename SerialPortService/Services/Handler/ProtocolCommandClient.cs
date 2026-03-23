using Logger.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 通用协议命令客户端。
    /// 负责统一命令构帧、请求发送、响应校验、业务解码与日志输出。
    /// </summary>
    public sealed class ProtocolCommandClient<TPacket> where TPacket : class
    {
        private readonly IRequestResponseContext<TPacket> _context;
        private readonly ILogger _logger;
        private readonly Func<TPacket, byte[]>? _rawFrameAccessor;
        private readonly string _tag;

        public ProtocolCommandClient(
            IRequestResponseContext<TPacket> context,
            ILogger? logger = null,
            Func<TPacket, byte[]>? rawFrameAccessor = null,
            string? protocolTag = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? NullLogger.Instance;
            _rawFrameAccessor = rawFrameAccessor;
            _tag = string.IsNullOrWhiteSpace(protocolTag) ? typeof(TPacket).Name : protocolTag!;
        }

        public async Task<TResult> ExecuteAsync<TResult>(
            IProtocolCommand<TPacket, TResult> command,
            int timeout = 1000,
            int retryCount = 3,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            var request = command.BuildRequest();
            LogFrame(LogLevel.Information, $"[{_tag} TX] ", request);

            var response = await _context
                .SendRequestAsync(request, timeout, retryCount, cancellationToken)
                .ConfigureAwait(false);

            var responseFrame = _rawFrameAccessor?.Invoke(response);
            if (responseFrame is { Length: > 0 })
            {
                LogFrame(LogLevel.Information, $"[{_tag} RX] ", responseFrame);
            }

            command.ValidateResponse(response);
            return command.DecodeResponse(response);
        }

        private void LogFrame(LogLevel level, string prefix, byte[] frame)
        {
            if (_logger.IsEnabled(level))
            {
                _logger.AddLog(level, string.Concat(prefix, BitConverter.ToString(frame)));
            }
        }
    }
}
