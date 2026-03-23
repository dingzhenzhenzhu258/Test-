using SerialPortService.Models.Enums;
using SerialPortService.Services.Protocols;

namespace SerialPortService.Services.Interfaces
{
    public interface IProtocolDefinitionRegistry
    {
        ProtocolDefinitionRegistrationResult Register<TPacket>(string key, IProtocolDefinition<TPacket> definition) where TPacket : class;

        bool TryGet<TPacket>(ProtocolEnum protocol, out IProtocolDefinition<TPacket>? definition) where TPacket : class;
    }
}

