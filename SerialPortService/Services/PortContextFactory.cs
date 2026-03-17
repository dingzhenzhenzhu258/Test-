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

        /// <summary>
        /// 设备类型到协议类型的动态推断函数。
        /// </summary>
        private static Func<HandleEnum, ProtocolEnum>? _protocolResolver;

        /// <summary>
        /// 用于创建各处理器日志实例的工厂。
        /// </summary>
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// 通用处理器基础配置。
        /// </summary>
        private readonly GenericHandlerOptions _options;

        /// <summary>
        /// 创建串口上下文工厂。
        /// </summary>
        /// <param name="loggerFactory">日志工厂</param>
        /// <param name="options">通用处理器基础配置</param>
        public PortContextFactory(ILoggerFactory loggerFactory, GenericHandlerOptions options)
        {
            _loggerFactory = loggerFactory;
            _options = options;
        }

        /// <summary>
        /// 注册外部设备处理器工厂。
        /// </summary>
        /// <param name="handleEnum">设备类型</param>
        /// <param name="factory">外部上下文工厂</param>
        /// <returns>是否注册成功</returns>
        public bool RegisterHandlerFactory(HandleEnum handleEnum, ISerialPortService.PortContextFactory factory)
            => _handlerFactories.TryAdd(handleEnum, factory);

        /// <summary>
        /// 设置协议推断函数。
        /// </summary>
        /// <param name="resolver">设备类型到协议类型的解析函数</param>
        public void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver)
            => _protocolResolver = resolver;

        /// <summary>
        /// 基于基础配置构建附带标签的 <see cref="GenericHandlerOptions"/>。
        /// </summary>
        /// <param name="handleEnum">设备类型标签</param>
        /// <param name="protocol">协议标签</param>
        /// <returns>附带标签的处理器配置</returns>
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
        /// <param name="portName">串口名称</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="parity">校验位</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="stopBits">停止位</param>
        /// <param name="handleEnum">设备类型</param>
        /// <param name="protocol">协议类型</param>
        /// <returns>上下文实例及最终解析出的协议类型</returns>
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
