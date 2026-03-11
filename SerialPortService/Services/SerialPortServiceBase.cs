using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SerialPortService.Models;
using SerialPortService.Models.Emuns;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SerialPortService.Services
{
    /// <summary>
    /// 串口服务基类。
    /// 负责串口上下文创建、协议路由、生命周期管理与外部调用入口封装。
    /// </summary>
    /// <remarks>
    /// 该类面向业务层提供统一 API（Open/Close/Write/TryGetContext），
    /// 并通过 <see cref="GenericHandlerOptions"/> 统一下发并发/限流/重连策略。
    /// </remarks>
    public class SerialPortServiceBase : ISerialPortService
    {
        private readonly record struct PortBinding(
            int BaudRate,
            Parity Parity,
            int DataBits,
            StopBits StopBits,
            HandleEnum Handle,
            ProtocolEnum Protocol,
            string? ParserType);

        private readonly ILoggerFactory _loggerFactory;
        private readonly GenericHandlerOptions _genericHandlerOptions;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private static readonly ConcurrentDictionary<HandleEnum, ISerialPortService.PortContextFactory> handlerFactories = new();
        private static Func<HandleEnum, ProtocolEnum>? protocolResolver;
        private static PortBinding? configuredReconnectPolicy;
        private static readonly object reconnectPolicyLock = new();
        private static readonly object portOpenCloseLock = new();

        public SerialPortServiceBase(ILoggerFactory? loggerFactory = null, GenericHandlerOptions? genericHandlerOptions = null)
        {
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<SerialPortServiceBase>();
            _genericHandlerOptions = genericHandlerOptions ?? new GenericHandlerOptions();

            // 步骤1：构建可比较的重连策略快照。
            // 为什么：需要在多实例场景中识别“谁改写了全局静态策略”。
            // 风险点：若不做快照比较，配置漂移会静默发生且难以排查。
            var currentReconnectPolicy = new PortBinding(
                _genericHandlerOptions.ReconnectIntervalMs,
                Parity.None,
                _genericHandlerOptions.MaxReconnectAttempts,
                StopBits.None,
                HandleEnum.Default,
                ProtocolEnum.Default,
                $"{_genericHandlerOptions.ReconnectFailureRateAlertThresholdPercent}:{_genericHandlerOptions.ReconnectFailureRateAlertMinSamples}");

            lock (reconnectPolicyLock)
            {
                // 步骤2：比较并记录策略漂移。
                // 为什么：SerialPortReconnectPolicy 为进程级静态配置，后创建实例可能覆盖前者。
                // 风险点：不同业务模块若使用不同重连参数，会出现“行为突然变化”。
                if (configuredReconnectPolicy.HasValue && configuredReconnectPolicy.Value != currentReconnectPolicy)
                {
                    _logger.LogWarning("SerialPortReconnectPolicy is global static and has been reconfigured by another SerialPortServiceBase instance.");
                }

                configuredReconnectPolicy = currentReconnectPolicy;
            }

            // 步骤3：下发全局重连参数。
            // 为什么：PortContext 的异常重连路径会读取该全局策略。
            // 风险点：若未及时下发，重连行为可能沿用旧值导致告警误判。
            SerialPortReconnectPolicy.Configure(
                _genericHandlerOptions.ReconnectIntervalMs,
                _genericHandlerOptions.MaxReconnectAttempts,
                _genericHandlerOptions.ReconnectFailureRateAlertThresholdPercent,
                _genericHandlerOptions.ReconnectFailureRateAlertMinSamples);

        }

        private GenericHandlerOptions CreateTaggedOptions(HandleEnum handleEnum, ProtocolEnum protocol)
        {
            // 步骤1：复制基础配置并附加标签。
            // 为什么：运行指标需区分设备类型和协议，便于定位热点问题。
            // 风险点：若标签缺失，不同设备数据会混淆在同一指标维度中。
            return new GenericHandlerOptions
            {
                ResponseChannelCapacity = _genericHandlerOptions.ResponseChannelCapacity,
                SampleLogInterval = _genericHandlerOptions.SampleLogInterval,
                DropWhenNoActiveRequest = _genericHandlerOptions.DropWhenNoActiveRequest,
                ResponseChannelFullMode = _genericHandlerOptions.ResponseChannelFullMode,
                WaitModeQueueCapacity = _genericHandlerOptions.WaitModeQueueCapacity,
                ProtocolTag = protocol.ToString(),
                DeviceTypeTag = handleEnum.ToString(),
                ReconnectIntervalMs = _genericHandlerOptions.ReconnectIntervalMs,
                MaxReconnectAttempts = _genericHandlerOptions.MaxReconnectAttempts,
                TimeoutRateAlertThresholdPercent = _genericHandlerOptions.TimeoutRateAlertThresholdPercent,
                TimeoutRateAlertMinSamples = _genericHandlerOptions.TimeoutRateAlertMinSamples,
                WaitBacklogAlertThreshold = _genericHandlerOptions.WaitBacklogAlertThreshold,
                ReconnectFailureRateAlertThresholdPercent = _genericHandlerOptions.ReconnectFailureRateAlertThresholdPercent,
                ReconnectFailureRateAlertMinSamples = _genericHandlerOptions.ReconnectFailureRateAlertMinSamples
            };
        }

        /// <summary>
        /// 串口上下文集合 key: 串口号 value: 串口上下文
        /// </summary>
        private static readonly ConcurrentDictionary<string, IPortContext> ports = new();
        private static readonly ConcurrentDictionary<string, PortBinding> portBindings = new();

        // 对外只暴露 IReadOnlyDictionary 接口
        public static IReadOnlyDictionary<string, IPortContext> OnlyReadports => ports;

        private (IPortContext Context, ProtocolEnum ResolvedProtocol) CreateContext(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol)
        {
            var resolvedProtocol = protocol;

            // 步骤1：在协议为 Default 时进行动态推断。
            // 为什么：业务层可只传设备类型，由服务层统一解析协议。
            // 风险点：推断规则缺失会导致上下文创建失败或协议不匹配。
            if (resolvedProtocol == ProtocolEnum.Default)
            {
                if (protocolResolver != null)
                {
                    resolvedProtocol = protocolResolver(handleEnum);
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

            if (handlerFactories.TryGetValue(handleEnum, out var factory))
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

        /// <summary>
        /// 打开串口 (使用预定义设备枚举)
        /// 如果 handleEnum 为 Default，则必须指定 protocol
        /// </summary>
        public OperateResult OpenPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum = HandleEnum.Default, ProtocolEnum protocol = ProtocolEnum.Default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                var resolvedProtocol = protocol;
                lock (portOpenCloseLock)
                {
                    // 步骤1：校验端口重开参数一致性。
                    // 为什么：同端口复用旧上下文时必须保证参数一致。
                    // 风险点：参数漂移会造成“看似成功打开，实际用旧参数通信”。
                    if (portBindings.TryGetValue(portName, out var existingBinding))
                    {
                        var requestedProtocol = protocol == ProtocolEnum.Default ? existingBinding.Protocol : protocol;
                        var requestedBinding = new PortBinding(baudRate, parity, dataBits, stopBits, handleEnum, requestedProtocol, ParserType: null);
                        if (existingBinding != requestedBinding)
                        {
                            return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                        }
                    }

                    // 步骤2：创建或复用端口上下文（GetOrAdd）。
                    // 为什么：在并发打开场景避免重复实例化上下文。
                    // 风险点：若无原子复用，可能出现多上下文竞争同一串口。
                    var context = ports.GetOrAdd(portName, _ =>
                    {
                        var result = CreateContext(portName, baudRate, parity, dataBits, stopBits, handleEnum, protocol);
                        resolvedProtocol = result.ResolvedProtocol;
                        return result.Context;
                    });

                    // 步骤3：双检绑定一致性并写入绑定快照。
                    // 为什么：防止并发路径造成“上下文与绑定参数不一致”。
                    // 风险点：绑定状态错误会影响后续重开校验与排障。
                    var binding = new PortBinding(baudRate, parity, dataBits, stopBits, handleEnum, resolvedProtocol, ParserType: null);
                    if (portBindings.TryGetValue(portName, out var currentBinding) && currentBinding != binding)
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }
                    portBindings.TryAdd(portName, binding);

                    // 步骤4：打开上下文并启动内部管线。
                    // 为什么：只有 Open 后才能启动 IO/解析/发送任务。
                    // 风险点：打开失败会进入异常路径，调用方需关注返回错误信息。
                    context.Open();
                }

                string info = $"串口 {portName} 打开成功，模式：{handleEnum} / {resolvedProtocol}，参数：波特率={baudRate}, 数据位={dataBits}, 校验位={parity}, 停止位={stopBits}";
                return new OperateResult(true, info, 0);
            }
            catch (Exception e)
            {
                return new OperateResult(false, $"串口打开失败：{e.Message}", -1);
            }
        }

        /// <summary>
        /// 打开串口 (使用自定义解析器)
        /// </summary>
        public OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                ArgumentNullException.ThrowIfNull(parser);

                lock (portOpenCloseLock)
                {
                    // 步骤1：将解析器类型纳入绑定一致性校验。
                    // 为什么：同端口不同解析器本质上是不同协议语义。
                    // 风险点：若忽略解析器类型，可能把旧解析器误用于新协议数据流。
                    var parserType = parser.GetType().FullName;
                    var expected = new PortBinding(baudRate, parity, dataBits, stopBits, HandleEnum.Default, ProtocolEnum.Default, parserType);
                    if (portBindings.TryGetValue(portName, out var existingBinding) && existingBinding != expected)
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }

                    // 步骤2：创建或复用 GenericHandler<T>。
                    // 为什么：复用通用并发、重试、告警和指标能力。
                    // 风险点：若复用了错误上下文，会导致解析错位或请求匹配失败。
                    var context = ports.GetOrAdd(portName, _ => new GenericHandler<T>(portName, baudRate, parity, dataBits, stopBits, parser, _loggerFactory.CreateLogger<GenericHandler<T>>(), CreateTaggedOptions(HandleEnum.Default, ProtocolEnum.Default)));
                    if (portBindings.TryGetValue(portName, out var currentBinding) && currentBinding != expected)
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }
                    portBindings.TryAdd(portName, expected);

                    // 步骤3：打开上下文。
                    // 为什么：触发底层读写任务，开始处理串口数据。
                    // 风险点：打开失败会返回错误结果，调用方需中止后续发送。
                    context.Open();
                }

                string info = $"串口 {portName} 打开成功 (自定义解析器)，参数：波特率={baudRate}, 数据位={dataBits}, 校验位={parity}, 停止位={stopBits}";
                return new OperateResult(true, info, 0);
            }
            catch (Exception e)
            {
                return new OperateResult(false, $"串口打开失败：{e.Message}", -1);
            }
        }

        /// <summary>
        /// 写对应串口数据
        /// </summary>
        /// <param name="portName"></param>
        /// <param name="data"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public async Task<byte[]> Write(string portName, byte[] data)
        {
            // 步骤1：采用异常语义执行发送。
            // 为什么：对“必须成功发送”的调用方，异常更直接。
            // 风险点：业务层若未捕获异常，可能中断当前流程。
            if (ports.TryGetValue(portName, out var context))
            {
                return await context.Send(data).ConfigureAwait(false);

            }
            else
                throw new InvalidOperationException($"串口 {portName} 未打开");
        }

        /// <summary>
        /// 非异常语义发送接口。
        /// 失败时返回 <see cref="OperateResult{T}"/>，便于业务统一处理。
        /// </summary>
        public async Task<OperateResult<byte[]>> TryWrite(string portName, byte[] data)
        {
            // 步骤1：执行前置参数校验。
            // 为什么：结果语义接口应优先返回可诊断失败信息。
            // 风险点：缺少前置校验会导致底层异常分散到多处处理。
            if (string.IsNullOrWhiteSpace(portName))
            {
                return new OperateResult<byte[]>(false, "串口名不能为空", -1);
            }

            if (data == null || data.Length == 0)
            {
                return new OperateResult<byte[]>(false, "发送数据不能为空", -1);
            }

            if (!ports.TryGetValue(portName, out var context))
            {
                return new OperateResult<byte[]>(false, $"串口 {portName} 未打开", -1);
            }

            try
            {
                // 步骤2：发送成功返回回显数据。
                // 为什么：回显可用于链路追踪与业务层审计。
                // 风险点：若直接吞掉回显，排障时难定位具体发送内容。
                var sent = await context.Send(data).ConfigureAwait(false);
                return new OperateResult<byte[]>(sent, true, "发送成功", 0);
            }
            catch (Exception ex)
            {
                // 步骤3：异常转为失败结果返回。
                // 为什么：统一业务层失败分支处理，减少散落 try/catch。
                // 风险点：若仅记录通用失败，需结合日志保留上下文细节。
                return new OperateResult<byte[]>(false, $"发送失败：{ex.Message}", -1);
            }
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        /// <param name="portName"></param>
        /// <returns></returns>
        public async Task<OperateResult> ClosePortAsync(string portName)
        {
            IPortContext? context;
            PortBinding? bindingSnapshot = null;

            // 步骤1：原子化摘除（防止后续 50ms 的采集任务还能拿到这个句柄）
            lock (portOpenCloseLock)
            {
                if (!ports.TryRemove(portName, out context))
                {
                    return new OperateResult(false, $"未找到串口 {portName}", -1);
                }

                if (portBindings.TryGetValue(portName, out var binding))
                {
                    bindingSnapshot = binding;
                }
                portBindings.TryRemove(portName, out _);
            }

            // 步骤2：强制执行物理关闭（移出 UI 线程防止界面卡死）
            try
            {
                var closeTask = Task.Run(() =>
                {
                    try
                    {
                        // 如果 context 内部能访问到 BaseStream，直接 Dispose 它
                        // 这比任何 Discard 都管用，它会强制 Windows 撤销所有 pending 的 IO 请求
                        context?.Close();
                        context?.Dispose();
                        return true;
                    }
                    catch (Exception ex)
                    { 

                        // 使用你的 LoggerHelper 扩展方法
                        _logger.AddLog(
                            level: LogLevel.Error,
                            messageTemplate: "串口 {PortName} 物理关闭异常",
                            isShowUI: false,          
                            exception: ex,           // 极其重要！传入这个 ex 才能在 OpenObserve 看到堆栈 [cite: 10]
                            args: ex.Message         // 结构化参数
                        );
                        return false;
                    }
                });

                // 3秒硬超时：专门对付那条“第67万条报文”引发的驱动层死锁
                if (await Task.WhenAny(closeTask, Task.Delay(3000)) != closeTask)
                {
                    // 使用你的 LoggerHelper 扩展方法
                    _logger.AddLog(
                        level: LogLevel.Warning,
                        messageTemplate: "警告：串口 {PortName} 驱动响应超时。已强行剥离逻辑关联，防止界面挂起。",
                        isShowUI: false,          
                        args: portName         // 结构化参数
                    );
                    // 注意：绝不执行 TryAdd 回滚，坏掉的串口必须从系统中彻底剔除
                    return new OperateResult(false, $"{portName} 驱动死锁：已强制放弃句柄", -2);
                }
            }
            catch (Exception ex)
            {
                // 使用你的 LoggerHelper 扩展方法
                _logger.AddLog(
                    level: LogLevel.Error,
                    messageTemplate: "关闭串口 {PortName} 时触发未知错误",
                    isShowUI: false,
                    exception: ex,           // 极其重要！传入这个 ex 才能在 OpenObserve 看到堆栈 [cite: 10]
                    args: ex.Message         // 结构化参数
                );
                return new OperateResult(false, $"{portName} 关闭失败: {ex.Message}", -1);
            }

            return new OperateResult(true, $"{portName} 关闭成功", 0);
        }

        /// <summary>
        /// 关闭全部已打开串口。
        /// </summary>
        public OperateResult CloseAll()
        {
            List<(string PortName, IPortContext Context)> contextsToClose;
            lock (portOpenCloseLock)
            {
                // 步骤1：快照当前端口并逐个关闭。
                // 为什么：避免遍历期间字典变化影响关闭流程。
                // 风险点：并发修改时若无快照，可能遗漏或重复关闭。
                var portNames = ports.Keys.ToList();
                contextsToClose = new List<(string PortName, IPortContext Context)>(portNames.Count);

                foreach (var portName in portNames)
                {
                    if (!ports.TryRemove(portName, out var context))
                        continue;

                    // 步骤2：同步清理绑定状态。
                    // 为什么：保持端口上下文与绑定快照的一致性。
                    // 风险点：状态不一致会影响后续 IsOpen/OpenPort 判断。
                    portBindings.TryRemove(portName, out _);

                    contextsToClose.Add((portName, context));
                }

                if (contextsToClose.Count == 0)
                    return new OperateResult(true, "关闭完成: 0", 0);

                // 锁内仅完成状态摘除，实际关闭在锁外执行。
            }

            var errors = new List<string>();
            foreach (var (portName, context) in contextsToClose)
            {
                try
                {
                    context.Close();
                    context.Dispose();
                }
                catch (Exception ex)
                {
                    errors.Add($"{portName}:{ex.Message}");
                }
            }

            if (errors.Count > 0)
                return new OperateResult(false, $"部分关闭失败: {string.Join("; ", errors)}", -1);

            return new OperateResult(true, $"关闭完成: {contextsToClose.Count}", 0);
        }

        /// <summary>
        /// 尝试获取串口上下文。
        /// </summary>
        public bool TryGetContext(string portName, out IPortContext? context)
        {
            return ports.TryGetValue(portName, out context);
        }

        /// <summary>
        /// 注册设备处理器工厂。
        /// </summary>
        public bool RegisterHandlerFactory(HandleEnum handleEnum, ISerialPortService.PortContextFactory factory)
        {
            return handlerFactories.TryAdd(handleEnum, factory);
        }

        /// <summary>
        /// 设置设备协议解析函数。
        /// </summary>
        public void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver)
        {
            protocolResolver = resolver;
        }

        /// <summary>
        /// 检查对应串口是否打开
        /// </summary>
        /// <param name="portName"></param>
        /// <returns></returns>
        public bool IsOpen(string portName) => ports.ContainsKey(portName);

        /// <summary>
        /// 刷新串口
        /// </summary>
        /// <param name="oldPortNames"></param>
        public void RefreshPorts(ref List<string> oldPortNames)
        {
            var current = SerialPort.GetPortNames();

            oldPortNames = oldPortNames.Intersect(current).ToList();
            oldPortNames.AddRange(current.Except(oldPortNames));
        }
    }
}
