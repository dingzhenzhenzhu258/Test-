using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Parser;
using SerialPortService.Services.Protocols.Modbus;
using System.Collections.Concurrent;

namespace SerialPortService.Services
{
    /// <summary>
    /// 实例级解析器注册表。
    /// </summary>
    public sealed class ParserFactory : IParserRegistry
    {
        private readonly ConcurrentDictionary<(ProtocolEnum Protocol, System.Type ResultType), ParserRegistration> _registrations = new();

        public ParserFactory()
        {
            Register<ModbusPacket>(ProtocolEnum.ModbusRTU, "builtin_modbus_rtu", static () => new ModbusRtuParser());
            Register<CustomFrame>(ProtocolEnum.Default, "builtin_custom_default", static () => new CustomProtocolParser());
        }

        public ParserRegistrationResult Register<T>(ProtocolEnum protocol, string key, Func<IStreamParser<T>> factory) where T : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(factory);

            var registrationKey = (protocol, typeof(T));
            var registration = new ParserRegistration(key, factory);
            if (_registrations.TryAdd(registrationKey, registration))
            {
                return new ParserRegistrationResult(true, $"Parser registration '{key}' added.", key);
            }

            var existing = _registrations[registrationKey];
            return new ParserRegistrationResult(
                false,
                $"Parser registration already exists for protocol={protocol}, resultType={typeof(T).Name}.",
                key,
                existing.Key);
        }

        public IStreamParser<T> Create<T>(ProtocolEnum protocol) where T : class
        {
            if (_registrations.TryGetValue((protocol, typeof(T)), out var registration))
            {
                return ((Func<IStreamParser<T>>)registration.Factory)();
            }

            throw new NotSupportedException($"No parser registered for protocol={protocol}, resultType={typeof(T).Name}.");
        }

        private readonly record struct ParserRegistration(string Key, Delegate Factory);
    }
}
