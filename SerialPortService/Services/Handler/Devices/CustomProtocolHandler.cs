using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Parser;
using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 自定义协议处理器示例：复用 <see cref="GenericHandler{T}"/> 的高并发、
    /// 限流、重试、超时、指标与重连能力。
    /// </summary>
    public class CustomProtocolHandler : GenericHandler<CustomFrame>, ICustomProtocolContext
    {
        /// <summary>
        /// 自定义协议响应匹配策略。
        /// 这里按命令字进行请求/响应配对。
        /// </summary>
        private sealed class CustomResponseMatcher : IResponseMatcher<CustomFrame>
        {
            /// <summary>
            /// 判断响应帧是否与请求命令匹配。
            /// </summary>
            public bool IsResponseMatch(CustomFrame response, byte[] command)
            {
                if (response == null || command == null || command.Length < 3)
                    return false;

                var expectedCommand = command[2];
                return response.Command == expectedCommand;
            }

            public bool IsReportPacket(CustomFrame response) => false;

            public void OnReportPacket(CustomFrame response) { }

            public string BuildUnmatchedLog(CustomFrame response)
                => $"Cmd=0x{response.Command:X2}, Raw={BitConverter.ToString(response.Raw)}";
        }

        /// <summary>
        /// 创建自定义协议处理器。
        /// </summary>
        public CustomProtocolHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, new CustomProtocolParser(), logger, matcher: new CustomResponseMatcher())
        {
        }

        /// <summary>
        /// 构建协议帧并发送请求，等待匹配响应。
        /// </summary>
        public async Task<CustomFrame> SendRequestAsync(byte command, byte[]? payload = null, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default)
        {
            // 步骤1：构建协议请求帧。
            // 为什么：统一由帧构建器保证报文字段和校验一致性。
            // 风险点：手工拼帧容易出现长度或校验错误。
            var request = CustomProtocolFrameBuilder.Build(command, payload ?? Array.Empty<byte>());

            // 步骤2：发送并等待匹配响应。
            // 为什么：复用 GenericHandler 的超时、重试、匹配与告警机制。
            // 风险点：超时参数配置不当会导致误重试或响应延迟。
            return await SendRequestCoreAsync(request, timeout, retryCount, cancellationToken);
        }
    }
}
