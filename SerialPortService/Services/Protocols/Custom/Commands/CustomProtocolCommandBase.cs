using SerialPortService.Models;
using SerialPortService.Services.Protocols;
using SerialPortService.Services.Parser;
using System;

namespace SerialPortService.Services.Protocols.Custom.Commands
{
    /// <summary>
    /// 自定义协议命令定义基类。
    /// </summary>
    public abstract class CustomProtocolCommandBase<TResult> : ICustomProtocolCommand<TResult>
    {
        protected CustomProtocolCommandBase(byte command, byte[]? payload = null)
        {
            Command = command;
            Payload = payload ?? Array.Empty<byte>();
        }

        protected byte Command { get; }
        protected byte[] Payload { get; }

        public byte CommandByte => Command;
        public byte[] PayloadBytes => Payload;

        public virtual byte[] BuildRequest() => CustomProtocolFrameBuilder.Build(Command, Payload);

        public virtual void ValidateResponse(CustomFrame response)
        {
            ArgumentNullException.ThrowIfNull(response);
            if (response.Command != Command)
            {
                throw new ProtocolMismatchException(
                    $"Unexpected CustomProtocol command: expected 0x{Command:X2}, got 0x{response.Command:X2} (Raw: {BitConverter.ToString(response.Raw)})");
            }
        }

        public abstract TResult DecodeResponse(CustomFrame response);
    }
}
