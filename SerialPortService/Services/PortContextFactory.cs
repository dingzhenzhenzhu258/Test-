using Microsoft.Extensions.Logging;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Concurrent;
using System.IO.Ports;

namespace SerialPortService.Services
{
    /// <summary>
    /// 串口上下文工厂。
    /// 负责根据设备类型、协议与参数创建对应的 <see cref="IPortContext"/> 实例。
    /// 从 <see cref="SerialPortServiceBase"/> 解耦，使两者职责单一。
    /// </summary>
    internal sealed class PortContextFactory
    {
        // 步骤1：保持静态以支持跨实例共享注册（与原行为一致）。
        // 为什么：业务方可在任意时刻注册外部工厂，不受服务实例生命周期约束。
        // 风险点：静态状态在多实例场景下会相互影响，注册前需确认唯一性。
        private static readonly ConcurrentDictionary<HandleEnum, ISerialPortService.PortContextFactory> _handlerFactories = new();
        private static Func<HandleEnum, ProtocolEnum>? _protocolResolver;

        private readonly ILoggerFactory _loggerFactory;
        private readonly GenericHandlerOptions _options;

        public PortContextFactory(ILoggerFactory loggerFactory, GenericHandlerOptions options)
        {
            _loggerFactory = loggerFactory;
            _options = options;
        }

        /// <summary>
        /// 注册外部设备处理器工厂。
        /// </summary>
        public bool RegisterHandlerFactory(HandleEnum handleEnum, ISerialPortService.PortContextFactory factory)
            => _handlerFactories.TryAdd(handleEnum, factory);

        /// <summary>
        /// 设置协议推断函数。
        /// </summary>
        public void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver)
            => _protocolResolver = resolver;

        /// <summary>
        /// 基于基础配置构建附带标签的 <see cref="GenericHandlerOptions"/>。
        /// </summary>
        public GenericHandlerOptions CreateTaggedOptions(HandleEnum handleEnum, ProtocolEnum protocol)
        {
            // 步骤1：复制基础配置并附加标签。
            // 为什么：运行指标需区分设备类型和协议，便于定位热点问题。
            // 风险点：若标签缺失，不同设备数据会混淆在同一指标维度中。
            return new GenericHandlerOptions
            {
                ResponseChannelCapacity = _options.ResponseChannelCapacity,
                SampleLogInterval = _options.SampleLogInterval,
                DropWhenNoActiveRequest = _options.DropWhenNoActiveRequest,
                ResponseChannelFullMode = _options.ResponseChannelFullMode,
                WaitModeQueueCapacity = _options.WaitModeQueueCapacity,
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

        /// <summary>
        /// 根据设备类型与协议创建 <see cref="IPortContext"/>。
        /// </summary>
        public (IPortContext Context, ProtocolEnum ResolvedProtocol) Create(
            string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits,
            HandleEnum handleEnum, ProtocolEnum protocol)
        {
            var resolvedProtocol = protocol;

            // 步骤1：在协议为 Default 时进行动态推断。
            // 为什么：业务层可只传设备类型，由服务层统一解析协议。
            // 风险点：推断规则缺失会导致上下文创建失败或协议不匹配。
            if (resolvedProtocol == ProtocolEnum.Default)
            {
                if (_protocolResolver != null)
                {
                    resolvedProtocol = _protocolResolver(handleEnum);
                }
                else
                {
                    switch (handleEnum)
                    {
                        case HandleEnum.TemperatureSensor:
                        case HandleEnum.ServoMotor:
                            resolvedProtocol = ProtocolEnum.ModbusRTU;
                            break;
                    }
                }
            }

            if (_handlerFactories.TryGetValue(handleEnum, out var factory))
            {
                // 步骤2：优先走外部工厂创建上下文。
                // 为什么：允许业务方扩展新设备处理器而不改库内核心代码。
                // 风险点：工厂实现不当可能引入线程安全或生命周期问题。
                return (factory(portName, baudRate, parity, dataBits, stopBits, handleEnum, resolvedProtocol, _loggerFactory), resolvedProtocol);
            }

            IPortContext context = handleEnum switch
            {
                HandleEnum.AudibleVisualAlarmHandler => new AudibleVisualAlarmHandler(portName, baudRate, parity, dataBits, stopBits, _loggerFactory.CreateLogger<AudibleVisualAlarmHandler>()),
                HandleEnum.BarcodeScanner => new BarcodeScannerHandler(portName, baudRate, parity, dataBits, stopBits, _loggerFactory.CreateLogger<BarcodeScannerHandler>()),
                HandleEnum.TemperatureSensor => new TemperatureSensorHandler(portName, baudRate, parity, dataBits, stopBits, ParserFactory.CreateModbusParser(resolvedProtocol), _loggerFactory.CreateLogger<TemperatureSensorHandler>()),
                HandleEnum.Default => resolvedProtocol == ProtocolEnum.ModbusRTU || resolvedProtocol == ProtocolEnum.ModbusASCII
                    ? new ModbusHandler(portName, baudRate, parity, dataBits, stopBits, _loggerFactory.CreateLogger<ModbusHandler>(), CreateTaggedOptions(handleEnum, resolvedProtocol))
                    : throw new InvalidOperationException("未指定设备类型，且协议不支持自动推断 Handler"),
                _ => throw new ArgumentOutOfRangeException(nameof(handleEnum))
            };

            return (context, resolvedProtocol);
        }
    }
}
