using SerialPortService.Models;
using SerialPortService.Services.Protocols;

namespace SerialPortService.Services.Protocols.Custom.Commands
{
    public interface ICustomProtocolCommand<TResult> : IProtocolCommand<CustomFrame, TResult>
    {
        byte CommandByte { get; }

        byte[] PayloadBytes { get; }
    }
}
