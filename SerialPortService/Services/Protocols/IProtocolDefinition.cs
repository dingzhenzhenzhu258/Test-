using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;

namespace SerialPortService.Services.Protocols
{
    public interface IProtocolDefinition<TPacket> where TPacket : class
    {
        string Name { get; }

        ProtocolEnum Protocol { get; }

        IStreamParser<TPacket> CreateParser();

        IResponseMatcher<TPacket> CreateResponseMatcher();

        byte[] GetRawFrame(TPacket packet);
    }
}
