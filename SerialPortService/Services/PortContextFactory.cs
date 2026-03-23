using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace SerialPortService.Services
{
    /// <summary>
    /// 串口上下文工厂。
    /// 负责根据设备类型、协议与参数创建对应的 <see cref="IPortContext"/> 实例。
    /// </summary>
    internal sealed class PortContextFactory
    {
        private readonly ConcurrentDictionary<string, IPortContextRegistration> _registrations = new();
        private Func<HandleEnum, ProtocolEnum>? _protocolResolver;
        private int _initialized;

        private readonly ILoggerFactory _loggerFactory;
        private readonly GenericHandlerOptions _options;
        private readonly IParserRegistry _parserRegistry;

        public PortContextFactory(ILoggerFactory loggerFactory, GenericHandlerOptions options, IParserRegistry parserRegistry)
        {
            _loggerFactory = loggerFactory;
            _options = options;
            _parserRegistry = parserRegistry;
            EnsureBuiltInRegistrations();
        }

        public ContextRegistrationResult RegisterContextRegistration(string key, IPortContextRegistration registration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(registration);

            if (_registrations.TryAdd(key, registration))
            {
                return new ContextRegistrationResult(true, $"Registration '{key}' added.", key);
            }

            return new ContextRegistrationResult(false, $"Registration '{key}' already exists.", key, key);
        }

        public void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver)
            => _protocolResolver = resolver;

        public GenericHandlerOptions CreateTaggedOptions(HandleEnum handleEnum, ProtocolEnum protocol)
        {
            return new GenericHandlerOptions
            {
                ResponseChannelCapacity = _options.ResponseChannelCapacity,
                SampleLogInterval = _options.SampleLogInterval,
                DropWhenNoActiveRequest = _options.DropWhenNoActiveRequest,
                ResponseChannelFullMode = _options.ResponseChannelFullMode,
                WaitModeQueueCapacity = _options.WaitModeQueueCapacity,
                SendChannelCapacity = _options.SendChannelCapacity,
                RawInputChannelCapacity = _options.RawInputChannelCapacity,
                RawReadBufferSize = _options.RawReadBufferSize,
                SerialPortReadBufferSize = _options.SerialPortReadBufferSize,
                EnableRawReadChunkLog = _options.EnableRawReadChunkLog,
                RawBytesLogIntervalSeconds = _options.RawBytesLogIntervalSeconds,
                DispatchParsedEventAsync = _options.DispatchParsedEventAsync,
                ParsedEventChannelCapacity = _options.ParsedEventChannelCapacity,
                ParsedEventChannelFullMode = _options.ParsedEventChannelFullMode,
                ProtocolTag = protocol.ToString(),
                DeviceTypeTag = handleEnum.ToString(),
                ReconnectIntervalMs = _options.ReconnectIntervalMs,
                MaxReconnectAttempts = _options.MaxReconnectAttempts,
                TimeoutRateAlertThresholdPercent = _options.TimeoutRateAlertThresholdPercent,
                TimeoutRateAlertMinSamples = _options.TimeoutRateAlertMinSamples,
                WaitBacklogAlertThreshold = _options.WaitBacklogAlertThreshold,
                ReconnectFailureRateAlertThresholdPercent = _options.ReconnectFailureRateAlertThresholdPercent,
                ReconnectFailureRateAlertMinSamples = _options.ReconnectFailureRateAlertMinSamples
            };
        }

        public (IPortContext Context, ProtocolEnum ResolvedProtocol) Create(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            HandleEnum handleEnum,
            ProtocolEnum protocol)
        {
            var resolvedProtocol = ResolveProtocol(handleEnum, protocol);

            var taggedOptions = CreateTaggedOptions(handleEnum, resolvedProtocol);
            var matchingRegistrations = GetOrderedRegistrations()
                .Where(r => r.Registration.CanHandle(handleEnum, resolvedProtocol))
                .ToList();

            if (matchingRegistrations.Count > 1)
            {
                _loggerFactory.CreateLogger<PortContextFactory>()
                    .LogWarning(
                        "Multiple context registrations matched handle={Handle}, protocol={Protocol}. Using {Winner}. Candidates={Candidates}",
                        handleEnum,
                        resolvedProtocol,
                        matchingRegistrations[0].Key,
                        string.Join(", ", matchingRegistrations.Select(x => x.Key)));
            }

            foreach (var entry in matchingRegistrations)
            {
                var context = entry.Registration.Create(
                    portName,
                    baudRate,
                    parity,
                    dataBits,
                    stopBits,
                    handleEnum,
                    resolvedProtocol,
                    _loggerFactory,
                    taggedOptions);

                return (context, resolvedProtocol);
            }

            if (handleEnum == HandleEnum.Default && resolvedProtocol == ProtocolEnum.ModbusASCII)
            {
                throw new NotSupportedException("当前版本尚未实现 ModbusASCII Handler，请改用 ModbusRTU 或自定义解析器。");
            }

            if (handleEnum == HandleEnum.Default)
            {
                throw new InvalidOperationException("未指定设备类型，且当前协议不支持自动推断 Handler");
            }

            throw new ArgumentOutOfRangeException(nameof(handleEnum));
        }

        private ProtocolEnum ResolveProtocol(HandleEnum handleEnum, ProtocolEnum protocol)
        {
            if (protocol != ProtocolEnum.Default)
            {
                return protocol;
            }

            if (_protocolResolver != null)
            {
                return _protocolResolver(handleEnum);
            }

            return handleEnum switch
            {
                HandleEnum.TemperatureSensor => ProtocolEnum.ModbusRTU,
                HandleEnum.ServoMotor => ProtocolEnum.ModbusRTU,
                _ => protocol
            };
        }

        private IReadOnlyList<(string Key, IPortContextRegistration Registration)> GetOrderedRegistrations()
            => _registrations
                .OrderBy(x => ParsePriority(x.Key))
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => (x.Key, x.Value))
                .ToList();

        private static int ParsePriority(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return int.MaxValue;
            }

            var prefix = key.Split('_', 2)[0];
            return int.TryParse(prefix, out var priority) ? priority : int.MaxValue;
        }

        private void EnsureBuiltInRegistrations()
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            {
                return;
            }

            _registrations.TryAdd("100_builtin_alarm", new ProtocolContextRegistration(
                (handle, _) => handle == HandleEnum.AudibleVisualAlarmHandler,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new AudibleVisualAlarmHandler(portName, baudRate, parity, dataBits, stopBits, loggerFactory.CreateLogger<AudibleVisualAlarmHandler>(), options)));

            _registrations.TryAdd("110_builtin_barcode", new ProtocolContextRegistration(
                (handle, _) => handle == HandleEnum.BarcodeScanner,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new BarcodeScannerHandler(portName, baudRate, parity, dataBits, stopBits, loggerFactory.CreateLogger<BarcodeScannerHandler>(), options)));

            _registrations.TryAdd("120_builtin_controller", new ProtocolContextRegistration(
                (handle, _) => handle == HandleEnum.Controller,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new ControllerHandler(portName, baudRate, parity, dataBits, stopBits, loggerFactory.CreateLogger<ControllerHandler>(), options)));

            _registrations.TryAdd("130_builtin_temperature_modbus", new ProtocolContextRegistration(
                (handle, protocol) => handle == HandleEnum.TemperatureSensor && protocol == ProtocolEnum.ModbusRTU,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new TemperatureSensorHandler(portName, baudRate, parity, dataBits, stopBits, _parserRegistry.Create<ModbusPacket>(protocol), loggerFactory.CreateLogger<TemperatureSensorHandler>(), options)));

            _registrations.TryAdd("140_builtin_servo_modbus", new ProtocolContextRegistration(
                (handle, protocol) => handle == HandleEnum.ServoMotor && protocol == ProtocolEnum.ModbusRTU,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new ModbusHandler(portName, baudRate, parity, dataBits, stopBits, _parserRegistry.Create<ModbusPacket>(protocol), loggerFactory.CreateLogger<ModbusHandler>(), options)));

            _registrations.TryAdd("150_builtin_custom_protocol", new ProtocolContextRegistration(
                (handle, _) => handle == HandleEnum.CustomProtocol,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new CustomProtocolHandler(portName, baudRate, parity, dataBits, stopBits, loggerFactory.CreateLogger<CustomProtocolHandler>(), options)));

            _registrations.TryAdd("160_builtin_default_modbus", new ProtocolContextRegistration(
                (handle, protocol) => handle == HandleEnum.Default && protocol == ProtocolEnum.ModbusRTU,
                (portName, baudRate, parity, dataBits, stopBits, handle, protocol, loggerFactory, options) =>
                    new ModbusHandler(portName, baudRate, parity, dataBits, stopBits, loggerFactory.CreateLogger<ModbusHandler>(), options)));
        }
    }
}
