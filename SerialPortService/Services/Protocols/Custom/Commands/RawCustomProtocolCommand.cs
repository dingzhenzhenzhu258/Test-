using SerialPortService.Models;

namespace SerialPortService.Services.Protocols.Custom.Commands
{
    /// <summary>
    /// 原样返回响应帧的自定义协议命令。
    /// 适用于尚未为具体命令补专用解码器的场景。
    /// </summary>
    public sealed class RawCustomProtocolCommand : CustomProtocolCommandBase<CustomFrame>
    {
        public RawCustomProtocolCommand(byte command, byte[]? payload = null)
            : base(command, payload)
        {
        }

        public override CustomFrame DecodeResponse(CustomFrame response) => response;
    }
}
