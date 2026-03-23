using SerialPortService.Models.Enums;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols;
using SerialPortService.Services.Protocols.Custom;
using SerialPortService.Services.Protocols.Modbus;
using System.Collections.Concurrent;

namespace SerialPortService.Services
{
    public sealed class ProtocolDefinitionRegistry : IProtocolDefinitionRegistry
    {
        private readonly ConcurrentDictionary<(ProtocolEnum Protocol, System.Type PacketType), Registration> _registrations = new();

        public ProtocolDefinitionRegistry()
        {
            Register("builtin_modbus_rtu", new ModbusProtocolDefinition());
            Register("builtin_custom_default", new CustomProtocolDefinition());
        }

        public ProtocolDefinitionRegistrationResult Register<TPacket>(string key, IProtocolDefinition<TPacket> definition) where TPacket : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(definition);

            var registrationKey = (definition.Protocol, typeof(TPacket));
            var registration = new Registration(key, definition);
            if (_registrations.TryAdd(registrationKey, registration))
            {
                return new ProtocolDefinitionRegistrationResult(true, $"Protocol definition '{key}' added.", key);
            }

            var existing = _registrations[registrationKey];
            return new ProtocolDefinitionRegistrationResult(false, $"Protocol definition already exists for protocol={definition.Protocol}, packetType={typeof(TPacket).Name}.", key, existing.Key);
        }

        public bool TryGet<TPacket>(ProtocolEnum protocol, out IProtocolDefinition<TPacket>? definition) where TPacket : class
        {
            if (_registrations.TryGetValue((protocol, typeof(TPacket)), out var registration))
            {
                definition = (IProtocolDefinition<TPacket>)registration.Definition;
                return true;
            }

            definition = null;
            return false;
        }

        private readonly record struct Registration(string Key, object Definition);
    }
}

