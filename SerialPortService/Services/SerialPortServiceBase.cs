using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Logger.Helpers;

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
    public class SerialPortServiceBase : ISerialPortService, IDisposable
    {
        private int _disposed;
        private sealed record PortBinding(
            int BaudRate,
            Parity Parity,
            int DataBits,
            StopBits StopBits,
            HandleEnum Handle,
            ProtocolEnum Protocol,
            string BindingKey,
            Func<IPortContext> ContextFactory,
            string? ParserDescription = null);

        private readonly ILoggerFactory _loggerFactory;
        private readonly GenericHandlerOptions _genericHandlerOptions;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly PortContextFactory _contextFactory;
        private readonly IParserRegistry _parserRegistry;
        private readonly IProtocolDefinitionRegistry _protocolDefinitions;
        private readonly object _portOpenCloseLock = new();
        private readonly ConcurrentDictionary<string, IPortContext> _ports = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PortBinding> _portBindings = new(StringComparer.OrdinalIgnoreCase);

        public SerialPortServiceBase(ILoggerFactory? loggerFactory = null, GenericHandlerOptions? genericHandlerOptions = null)
        {
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<SerialPortServiceBase>();
            _genericHandlerOptions = genericHandlerOptions ?? new GenericHandlerOptions();
            _parserRegistry = new ParserFactory();
            _protocolDefinitions = new ProtocolDefinitionRegistry();

            _contextFactory = new PortContextFactory(_loggerFactory, _genericHandlerOptions, _parserRegistry);
        }

        public IReadOnlyDictionary<string, IPortContext> OpenedPorts => _ports;

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
                lock (_portOpenCloseLock)
                {
                    // 步骤1：校验端口重开参数一致性。
                    // 为什么：同端口复用旧上下文时必须保证参数一致。
                    // 风险点：参数漂移会造成“看似成功打开，实际用旧参数通信”。
                    if (_portBindings.TryGetValue(portName, out var existingBinding))
                    {
                        var requestedProtocol = protocol == ProtocolEnum.Default ? existingBinding.Protocol : protocol;
                        var requestedBindingKey = BuildBindingKey(baudRate, parity, dataBits, stopBits, handleEnum, requestedProtocol, parserDescription: null);
                        if (!string.Equals(existingBinding.BindingKey, requestedBindingKey, StringComparison.Ordinal))
                        {
                            return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                        }
                    }

                    // 步骤2：创建或复用端口上下文（GetOrAdd）。
                    // 为什么：在并发打开场景避免重复实例化上下文。
                    // 风险点：若无原子复用，可能出现多上下文竞争同一串口。
                    var context = _ports.GetOrAdd(portName, _ =>
                    {
                        var result = _contextFactory.Create(portName, baudRate, parity, dataBits, stopBits, handleEnum, protocol);
                        resolvedProtocol = result.ResolvedProtocol;
                        return result.Context;
                    });

                    // 步骤3：双检绑定一致性并写入绑定快照。
                    // 为什么：防止并发路径造成“上下文与绑定参数不一致”。
                    // 风险点：绑定状态错误会影响后续重开校验与排障。
                    var binding = CreateBuiltInBinding(portName, baudRate, parity, dataBits, stopBits, handleEnum, resolvedProtocol);
                    if (_portBindings.TryGetValue(portName, out var currentBinding)
                        && !string.Equals(currentBinding.BindingKey, binding.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }
                    _portBindings.TryAdd(portName, binding);

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
            => OpenPort(portName, baudRate, parity, dataBits, stopBits, CreateReusableParserFactory(parser));

        /// <summary>
        /// 打开串口 (使用自定义解析器工厂)
        /// </summary>
        public OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory) where T : class
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                ArgumentNullException.ThrowIfNull(parserFactory);

                lock (_portOpenCloseLock)
                {
                    // 步骤1：将解析器类型纳入绑定一致性校验。
                    // 为什么：同端口不同解析器本质上是不同协议语义。
                    // 风险点：若忽略解析器类型，可能把旧解析器误用于新协议数据流。
                    var parser = CreateParser(parserFactory);
                    var expected = CreateCustomBinding(portName, baudRate, parity, dataBits, stopBits, parserFactory, parser);
                    if (_portBindings.TryGetValue(portName, out var existingBinding)
                        && !string.Equals(existingBinding.BindingKey, expected.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }

                    // 步骤2：创建或复用 GenericHandler<T>。
                    // 为什么：复用通用并发、重试、告警和指标能力。
                    // 风险点：若复用了错误上下文，会导致解析错位或请求匹配失败。
                    var context = _ports.GetOrAdd(portName, _ => CreateCustomParserContext(portName, baudRate, parity, dataBits, stopBits, parserFactory, parser));
                    if (_portBindings.TryGetValue(portName, out var currentBinding)
                        && !string.Equals(currentBinding.BindingKey, expected.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }
                    _portBindings.TryAdd(portName, expected);

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
        /// 异步打开串口（使用预定义设备枚举）。
        /// 推荐在非 UI 线程或 async 上下文中使用，避免 sync-over-async 阻塞。
        /// </summary>
        public async Task<OperateResult> OpenPortAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum = HandleEnum.Default, ProtocolEnum protocol = ProtocolEnum.Default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                var resolvedProtocol = protocol;
                IPortContext context;

                lock (_portOpenCloseLock)
                {
                    // 步骤1：校验端口重开参数一致性（同 OpenPort）。
                    // 为什么：同端口复用旧上下文时必须保证参数一致。
                    // 风险点：参数漂移会造成"看似成功打开，实际用旧参数通信"。
                    if (_portBindings.TryGetValue(portName, out var existingBinding))
                    {
                        var requestedProtocol = protocol == ProtocolEnum.Default ? existingBinding.Protocol : protocol;
                        var requestedBindingKey = BuildBindingKey(baudRate, parity, dataBits, stopBits, handleEnum, requestedProtocol, parserDescription: null);
                        if (!string.Equals(existingBinding.BindingKey, requestedBindingKey, StringComparison.Ordinal))
                        {
                            return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                        }
                    }

                    // 步骤2：创建或复用端口上下文。
                    // 为什么：在并发打开场景避免重复实例化上下文。
                    // 风险点：若无原子复用，可能出现多上下文竞争同一串口。
                    context = _ports.GetOrAdd(portName, _ =>
                    {
                        var result = _contextFactory.Create(portName, baudRate, parity, dataBits, stopBits, handleEnum, protocol);
                        resolvedProtocol = result.ResolvedProtocol;
                        return result.Context;
                    });

                    // 步骤3：双检绑定一致性并写入绑定快照。
                    // 为什么：防止并发路径造成"上下文与绑定参数不一致"。
                    // 风险点：绑定状态错误会影响后续重开校验与排障。
                    var binding = CreateBuiltInBinding(portName, baudRate, parity, dataBits, stopBits, handleEnum, resolvedProtocol);
                    if (_portBindings.TryGetValue(portName, out var currentBinding)
                        && !string.Equals(currentBinding.BindingKey, binding.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }
                    _portBindings.TryAdd(portName, binding);
                }

                // 步骤4：在锁外异步打开上下文，避免长时间持锁。
                // 为什么：Open 内含重试等待，持锁期间会阻塞所有 OpenPort/ClosePort 请求。
                // 风险点：锁外打开存在极短窗口期，但 OpenAsync 内部有幂等保护。
                await context.OpenAsync().ConfigureAwait(false);

                string info = $"串口 {portName} 打开成功，模式：{handleEnum} / {resolvedProtocol}，参数：波特率={baudRate}, 数据位={dataBits}, 校验位={parity}, 停止位={stopBits}";
                return new OperateResult(true, info, 0);
            }
            catch (Exception e)
            {
                return new OperateResult(false, $"串口打开失败：{e.Message}", -1);
            }
        }

        /// <summary>
        /// 异步打开串口（使用自定义解析器）。
        /// </summary>
        public async Task<OperateResult> OpenPortAsync<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class
            => await OpenPortAsync(portName, baudRate, parity, dataBits, stopBits, CreateReusableParserFactory(parser)).ConfigureAwait(false);

        /// <summary>
        /// 异步打开串口（使用自定义解析器工厂）。
        /// </summary>
        public async Task<OperateResult> OpenPortAsync<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory) where T : class
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                ArgumentNullException.ThrowIfNull(parserFactory);

                IPortContext context;

                lock (_portOpenCloseLock)
                {
                    // 步骤1：将解析器类型纳入绑定一致性校验。
                    // 为什么：同端口不同解析器本质上是不同协议语义。
                    // 风险点：若忽略解析器类型，可能把旧解析器误用于新协议数据流。
                    var parser = CreateParser(parserFactory);
                    var expected = CreateCustomBinding(portName, baudRate, parity, dataBits, stopBits, parserFactory, parser);
                    if (_portBindings.TryGetValue(portName, out var existingBinding)
                        && !string.Equals(existingBinding.BindingKey, expected.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }

                    // 步骤2：创建或复用 GenericHandler<T>。
                    // 为什么：复用通用并发、重试、告警和指标能力。
                    // 风险点：若复用了错误上下文，会导致解析错位或请求匹配失败。
                    context = _ports.GetOrAdd(portName, _ => CreateCustomParserContext(portName, baudRate, parity, dataBits, stopBits, parserFactory, parser));
                    if (_portBindings.TryGetValue(portName, out var currentBinding)
                        && !string.Equals(currentBinding.BindingKey, expected.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }
                    _portBindings.TryAdd(portName, expected);
                }

                // 步骤3：在锁外异步打开上下文。
                // 为什么：避免长时间持锁阻塞其他端口操作。
                // 风险点：锁外打开存在极短窗口期，但 OpenAsync 内部有幂等保护。
                await context.OpenAsync().ConfigureAwait(false);

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
            if (_ports.TryGetValue(portName, out var context))
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

            if (!_ports.TryGetValue(portName, out var context))
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

            // 步骤1：原子化摘除（防止后续 50ms 的采集任务还能拿到这个句柄）
            lock (_portOpenCloseLock)
            {
                if (!_ports.TryRemove(portName, out context))
                {
                    return new OperateResult(false, $"未找到串口 {portName}", -1);
                }

                _portBindings.TryRemove(portName, out _);
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
                using var timeoutCts = new CancellationTokenSource();
                var delayTask = Task.Delay(3000, timeoutCts.Token);
                if (await Task.WhenAny(closeTask, delayTask).ConfigureAwait(false) != closeTask)
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
                // closeTask 先完成时取消 delay Timer
                await timeoutCts.CancelAsync().ConfigureAwait(false);
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
            lock (_portOpenCloseLock)
            {
                // 步骤1：快照当前端口并逐个关闭。
                // 为什么：避免遍历期间字典变化影响关闭流程。
                // 风险点：并发修改时若无快照，可能遗漏或重复关闭。
                var portNames = _ports.Keys.ToList();
                contextsToClose = new List<(string PortName, IPortContext Context)>(portNames.Count);

                foreach (var portName in portNames)
                {
                    if (!_ports.TryRemove(portName, out var context))
                        continue;

                    // 步骤2：同步清理绑定状态。
                    // 为什么：保持端口上下文与绑定快照的一致性。
                    // 风险点：状态不一致会影响后续 IsOpen/OpenPort 判断。
                    _portBindings.TryRemove(portName, out _);

                    contextsToClose.Add((portName, context));
                }

                if (contextsToClose.Count == 0)
                    return new OperateResult(true, "关闭完成: 0", 0);

                // 锁内仅完成状态摘除，实际关闭在锁外执行。
            }

            var errors = new List<string>();
            foreach (var (portName, context) in contextsToClose)
            {
                // 步骤3：每个端口限时 3s 关闭，防止单口驱动死锁卡住整个关闭序列。
                // 为什么：与 ClosePortAsync 保持一致的超时兜底策略。
                // 风险点：超时后句柄可能残留，但避免了进程级阻塞。
                try
                {
                    var completed = Task.Run(() =>
                    {
                        context.Close();
                        context.Dispose();
                    }).Wait(3000);

                    if (!completed)
                    {
                        errors.Add($"{portName}: 驱动响应超时，已强制放弃");
                        _logger.AddLog(
                            level: LogLevel.Warning,
                            messageTemplate: "CloseAll: 串口 {PortName} 关闭超时（3s），已强制跳过",
                            isShowUI: false,
                            args: portName);
                    }
                }
                catch (AggregateException aex)
                {
                    errors.Add($"{portName}:{aex.InnerException?.Message ?? aex.Message}");
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
        /// 异步关闭全部已打开串口，每个端口限时 3s 防止驱动死锁。
        /// </summary>
        public async Task<OperateResult> CloseAllAsync()
        {
            List<(string PortName, IPortContext Context)> contextsToClose;
            lock (_portOpenCloseLock)
            {
                // 步骤1：快照当前端口并原子摘除。
                // 为什么：避免遍历期间字典变化影响关闭流程。
                // 风险点：并发修改时若无快照，可能遗漏或重复关闭。
                var portNames = _ports.Keys.ToList();
                contextsToClose = new List<(string PortName, IPortContext Context)>(portNames.Count);

                foreach (var portName in portNames)
                {
                    if (!_ports.TryRemove(portName, out var ctx))
                        continue;
                    _portBindings.TryRemove(portName, out _);
                    contextsToClose.Add((portName, ctx));
                }

                if (contextsToClose.Count == 0)
                    return new OperateResult(true, "关闭完成: 0", 0);
            }

            var errors = new List<string>();
            foreach (var (portName, context) in contextsToClose)
            {
                // 步骤2：每个端口限时 3s 异步关闭。
                // 为什么：与 ClosePortAsync 保持一致的超时兜底策略。
                // 风险点：超时后句柄可能残留，但避免了进程级阻塞。
                try
                {
                    var closeTask = Task.Run(() =>
                    {
                        context.Close();
                        context.Dispose();
                    });

                    using var timeoutCts = new CancellationTokenSource();
                    var delayTask = Task.Delay(3000, timeoutCts.Token);

                    if (await Task.WhenAny(closeTask, delayTask).ConfigureAwait(false) != closeTask)
                    {
                        errors.Add($"{portName}: 驱动响应超时，已强制放弃");
                        _logger.AddLog(
                            level: LogLevel.Warning,
                            messageTemplate: "CloseAllAsync: 串口 {PortName} 关闭超时（3s），已强制跳过",
                            isShowUI: false,
                            args: portName);
                    }
                    else
                    {
                        await timeoutCts.CancelAsync().ConfigureAwait(false);
                        await closeTask.ConfigureAwait(false);
                    }
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
            return _ports.TryGetValue(portName, out context);
        }

        public ContextRegistrationResult RegisterContextRegistration(string key, IPortContextRegistration registration)
            => _contextFactory.RegisterContextRegistration(key, registration);

        public ParserRegistrationResult RegisterParser<T>(ProtocolEnum protocol, string key, Func<IStreamParser<T>> factory) where T : class
            => _parserRegistry.Register(protocol, key, factory);

        public ProtocolDefinitionRegistrationResult RegisterProtocolDefinition<TPacket>(string key, IProtocolDefinition<TPacket> definition) where TPacket : class
            => _protocolDefinitions.Register(key, definition);

        public bool TryGetProtocolDefinition<TPacket>(ProtocolEnum protocol, out IProtocolDefinition<TPacket>? definition) where TPacket : class
            => _protocolDefinitions.TryGet(protocol, out definition);

        /// <summary>
        /// 设置设备协议解析函数。
        /// </summary>
        public void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver)
            => _contextFactory.SetProtocolResolver(resolver);

        /// <summary>
        /// 检查对应串口是否打开
        /// </summary>
        /// <param name="portName"></param>
        /// <returns></returns>
        public bool IsOpen(string portName) => _ports.ContainsKey(portName);

        public ServiceHealthSnapshot GetHealthSnapshot()
        {
            var snapshots = _ports.Values
                .OfType<IPortRuntimeDiagnostics>()
                .Select(x => x.GetRuntimeSnapshot())
                .OrderBy(x => x.PortName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ServiceHealthSnapshot(
                _ports.Count,
                snapshots.Count(x => x.IsRunning),
                snapshots.Count(x => !x.LastCloseSucceeded || x.CloseState is PortCloseState.Faulted or PortCloseState.TimedOut),
                snapshots);
        }

        public ServiceDiagnosticReport GetDiagnosticReport(int maxItems = 20)
        {
            if (maxItems <= 0)
            {
                maxItems = 20;
            }

            var health = GetHealthSnapshot();
            var recentErrors = health.Ports
                .SelectMany(x => x.RecentErrors)
                .OrderByDescending(x => x.UtcTicks)
                .Take(maxItems)
                .ToList();

            var recentEvents = health.Ports
                .SelectMany(x => x.RecentEvents)
                .OrderByDescending(x => x.UtcTicks)
                .Take(maxItems)
                .ToList();

            return new ServiceDiagnosticReport(
                health.HealthStatus,
                health.OpenPortCount,
                health.RunningPortCount,
                health.FaultedPortCount,
                recentErrors,
                recentEvents);
        }

        public PortRuntimeSnapshotResult GetPortRuntimeSnapshot(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return new PortRuntimeSnapshotResult(false, "串口名不能为空");
            }

            if (!_ports.TryGetValue(portName, out var context))
            {
                return new PortRuntimeSnapshotResult(false, $"未找到串口 {portName}");
            }

            if (context is not IPortRuntimeDiagnostics diagnostics)
            {
                return new PortRuntimeSnapshotResult(false, $"串口 {portName} 不支持运行时诊断");
            }

            return new PortRuntimeSnapshotResult(true, "获取成功", diagnostics.GetRuntimeSnapshot());
        }

        public async Task<OperateResult> RestartPortAsync(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return new OperateResult(false, "串口名不能为空", -1);
            }

            if (!_portBindings.TryGetValue(portName, out var binding))
            {
                return new OperateResult(false, $"未找到串口 {portName} 的打开参数", -1);
            }

            var closeResult = await ClosePortAsync(portName).ConfigureAwait(false);
            if (!closeResult.IsSuccess)
            {
                return new OperateResult(false, $"重启失败，关闭阶段失败：{closeResult.Message}", closeResult.ErrorCode);
            }

            return await ReopenBindingAsync(portName, binding).ConfigureAwait(false);
        }

        public async Task<BatchPortOperationResult> RestartPortsAsync(IEnumerable<string> portNames)
        {
            ArgumentNullException.ThrowIfNull(portNames);

            var results = new List<OperateResult>();
            foreach (var portName in portNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                results.Add(await RestartPortAsync(portName).ConfigureAwait(false));
            }

            return new BatchPortOperationResult(
                results.Count(x => x.IsSuccess),
                results.Count(x => !x.IsSuccess),
                results);
        }

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

        private async Task<OperateResult> ReopenBindingAsync(string portName, PortBinding binding)
        {
            try
            {
                IPortContext context;
                lock (_portOpenCloseLock)
                {
                    context = _ports.GetOrAdd(portName, _ => binding.ContextFactory());
                    if (_portBindings.TryGetValue(portName, out var currentBinding)
                        && !string.Equals(currentBinding.BindingKey, binding.BindingKey, StringComparison.Ordinal))
                    {
                        return new OperateResult(false, $"串口 {portName} 已按不同参数打开，请先关闭再重新打开", -1);
                    }

                    _portBindings.TryAdd(portName, binding);
                }

                await context.OpenAsync().ConfigureAwait(false);
                return new OperateResult(true, $"串口 {portName} 重启成功", 0);
            }
            catch (Exception ex)
            {
                lock (_portOpenCloseLock)
                {
                    _ports.TryRemove(portName, out _);
                    _portBindings.TryRemove(portName, out _);
                }

                return new OperateResult(false, $"重启失败，重开阶段失败：{ex.Message}", -1);
            }
        }

        private PortBinding CreateBuiltInBinding(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol)
            => new(
                baudRate,
                parity,
                dataBits,
                stopBits,
                handleEnum,
                protocol,
                BuildBindingKey(baudRate, parity, dataBits, stopBits, handleEnum, protocol, parserDescription: null),
                () => _contextFactory.Create(portName, baudRate, parity, dataBits, stopBits, handleEnum, protocol).Context);

        private PortBinding CreateCustomBinding<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory, IStreamParser<T> parser) where T : class
        {
            var parserDescription = parser.GetType().AssemblyQualifiedName ?? parser.GetType().FullName ?? typeof(T).FullName ?? typeof(T).Name;
            return new PortBinding(
                baudRate,
                parity,
                dataBits,
                stopBits,
                HandleEnum.Default,
                ProtocolEnum.Default,
                BuildBindingKey(baudRate, parity, dataBits, stopBits, HandleEnum.Default, ProtocolEnum.Default, parserDescription),
                () => CreateCustomParserContext(portName, baudRate, parity, dataBits, stopBits, parserFactory),
                parserDescription);
        }

        protected virtual IPortContext CreateCustomParserContext<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory, IStreamParser<T>? parser = null) where T : class
            => CreateGenericHandler(portName, baudRate, parity, dataBits, stopBits, parserFactory, parser);

        private GenericHandler<T> CreateGenericHandler<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory, IStreamParser<T>? parser = null) where T : class
            => new(
                portName,
                baudRate,
                parity,
                dataBits,
                stopBits,
                parser ?? CreateParser(parserFactory),
                _loggerFactory.CreateLogger<GenericHandler<T>>(),
                _contextFactory.CreateTaggedOptions(HandleEnum.Default, ProtocolEnum.Default));

        private static IStreamParser<T> CreateParser<T>(Func<IStreamParser<T>> parserFactory) where T : class
        {
            var parser = parserFactory();
            ArgumentNullException.ThrowIfNull(parser);
            parser.Reset();
            return parser;
        }

        private static Func<IStreamParser<T>> CreateReusableParserFactory<T>(IStreamParser<T> parser) where T : class
        {
            ArgumentNullException.ThrowIfNull(parser);
            return () =>
            {
                parser.Reset();
                return parser;
            };
        }

        private static string BuildBindingKey(int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, string? parserDescription)
            => FormattableString.Invariant($"{baudRate}|{(int)parity}|{dataBits}|{(int)stopBits}|{(int)handleEnum}|{(int)protocol}|{parserDescription ?? string.Empty}");

        /// <summary>
        /// 释放服务持有的全部串口资源。
        /// </summary>
        public void Dispose()
        {
            // 步骤1：幂等保护，避免重复释放。
            // 为什么：多次 Dispose 在 DI 容器销毁时可能发生。
            // 风险点：重复关闭会产生冗余日志和竞态异常。
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            CloseAll();
        }
    }
}
