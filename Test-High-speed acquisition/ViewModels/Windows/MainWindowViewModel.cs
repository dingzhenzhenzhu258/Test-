using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialPortService.Helpers;
using SerialPortService.Models;
using SerialPortService.Models.Emuns;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using Test_High_speed_acquisition.ViewModels.Models;
using Microsoft.Extensions.Logging;
using Logger.Helpers;

namespace Test_High_speed_acquisition.ViewModels.Windows
{
    /// <summary>
    /// 主窗口视图模型：负责串口枚举、打开/关闭串口、接收数据展示等交互逻辑。
    /// </summary>
    public partial class MainWindowViewModel : ViewModel
    {
        private const int UiFlushIntervalMs = 2000;
        private const int MinPollIntervalMs = 20;
        private readonly ISerialPortService _serialPortService;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _acquisitionCts;
        private Task? _sendLoopTask;
        private Task? _receiveLoopTask;
        private Task? _diagnosticTask;
        private Task[]? _persistWorkerTasks;
        private Channel<ModbusPacket>? _persistQueue;
        private IModbusContext? _modbusContext;
        private readonly Channel<string> _uiLines;
        private readonly DispatcherTimer _uiFlushTimer;
        private readonly StringBuilder _receivedDataBuffer = new(130_000);
        private long _totalSendCount;
        private long _totalReceiveCount;
        private long _segmentSendCount;
        private long _segmentReceiveCount;
        private int _segmentIndex;
        private readonly ILogger<MainWindowViewModel> _logger;
        private const int PersistWorkerCount = 4;
        private const int PersistBatchSize = 200;
        private const int PersistBatchWindowMs = 500;
        private const int DiagnosticIntervalMs = 5000;

        public MainWindowViewModel(ISerialPortService serialPortService, Dispatcher dispatcher, ILogger<MainWindowViewModel> logger)
        {
            _serialPortService = serialPortService;
            _dispatcher = dispatcher;

            _uiLines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = true
            });

            _logger = logger;

            _uiFlushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(UiFlushIntervalMs)
            };
            _uiFlushTimer.Tick += OnUiFlushTimerTick;
            _uiFlushTimer.Start();

            RefreshPorts();
            if (AvailablePorts.Count > 0)
            {
                SelectedPort = AvailablePorts[0];
            }
        }

        private async Task ReceiveLoopAsync(IModbusContext modbus, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var packet in modbus.ReadParsedPacketsAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_persistQueue == null)
                    {
                        continue;
                    }

                    await _persistQueue.Writer.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.AddLog(LogLevel.Information, "接收循环已取消", exception: ex);
            }
        }

        private async Task PersistWorkerLoopAsync(CancellationToken cancellationToken)
        {
            if (_persistQueue == null)
            {
                return;
            }

            var batch = new List<ModbusPacket>(PersistBatchSize);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var hasData = await _persistQueue.Reader
                        .WaitToReadAsync(cancellationToken)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromMilliseconds(PersistBatchWindowMs), cancellationToken)
                        .ConfigureAwait(false);

                    if (hasData)
                    {
                        while (batch.Count < PersistBatchSize && _persistQueue.Reader.TryRead(out var packet))
                        {
                            batch.Add(packet);
                        }
                    }
                }
                catch (TimeoutException ex)
                {
                    _logger.AddLog(LogLevel.Debug, "持久化批处理等待超时，继续下一轮", exception: ex);
                }
                catch (OperationCanceledException ex)
                {
                    _logger.AddLog(LogLevel.Information, "持久化工作循环已取消", exception: ex);
                    break;
                }

                if (batch.Count > 0)
                {
                    await PersistBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await PersistBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private Task PersistBatchAsync(List<ModbusPacket> batch, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Persist batch size={BatchSize}", batch.Count);
            return Task.CompletedTask;
        }

        private async Task DiagnosticLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DiagnosticIntervalMs, cancellationToken).ConfigureAwait(false);

                    var memoryMb = GC.GetTotalMemory(false) / 1024d / 1024d;
                    var threadCount = Process.GetCurrentProcess().Threads.Count;
                    var gc0 = GC.CollectionCount(0);
                    var gc1 = GC.CollectionCount(1);
                    var gc2 = GC.CollectionCount(2);

                    if (_modbusContext is ModbusHandler handler)
                    {
                        var metrics = handler.GetMetrics();
                        var parsedDrop = handler.GetParsedPacketDropCount();
                        _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] DIAG waitBacklog={metrics.WaitBacklog}, parsedPacketDrop={parsedDrop}, mem={memoryMb:F1}MB, threads={threadCount}, gc={gc0}/{gc1}/{gc2}");
                    }
                    else
                    {
                        _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] DIAG mem={memoryMb:F1}MB, threads={threadCount}, gc={gc0}/{gc1}/{gc2}");
                    }
                }
                catch (OperationCanceledException ex)
                {
                    _logger.AddLog(LogLevel.Information, "诊断循环已取消", exception: ex);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DiagnosticLoopAsync failed");
                }
            }
        }

        public ObservableCollection<string> AvailablePorts { get; } = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenPortCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClosePortCommand))]
        private string? selectedPort;

        [ObservableProperty]
        private int baudRate = 115200;

        [ObservableProperty]
        private int pollIntervalMs = 5;

        [ObservableProperty]
        private int slaveId = 1;

        [ObservableProperty]
        private int functionCode = 3;

        [ObservableProperty]
        private int startAddress;

        [ObservableProperty]
        private int registerCount = 2;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenPortCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClosePortCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartAcquisitionCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopAcquisitionCommand))]
        private bool isPortOpened;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartAcquisitionCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopAcquisitionCommand))]
        private bool isAcquiring;

        [ObservableProperty]
        private string receivedData = string.Empty;

        [ObservableProperty]
        private string statusMessage = "未连接";

        /// <summary>
        /// 是否允许打开串口。
        /// </summary>
        private bool CanOpenPort() => !IsPortOpened && !string.IsNullOrWhiteSpace(SelectedPort);

        /// <summary>
        /// 是否允许关闭串口。
        /// </summary>
        private bool CanClosePort() => IsPortOpened && !string.IsNullOrWhiteSpace(SelectedPort);

        private bool CanStartAcquisition() => IsPortOpened && !IsAcquiring;

        private bool CanStopAcquisition() => IsPortOpened && IsAcquiring;

        /// <summary>
        /// 刷新本机串口列表。
        /// </summary>
        [RelayCommand]
        private void RefreshPorts()
        {
            var values = AvailablePorts.ToList();
            _serialPortService.RefreshPorts(ref values);

            AvailablePorts.Clear();
            foreach (var item in values)
            {
                AvailablePorts.Add(item);
            }

            if (string.IsNullOrWhiteSpace(SelectedPort) || !AvailablePorts.Contains(SelectedPort))
            {
                SelectedPort = AvailablePorts.FirstOrDefault();
            }
        }

        /// <summary>
        /// 打开串口并启动高速采集。
        /// 发送线程持续发送读命令，接收线程持续消费解析报文。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanOpenPort))]
        private async Task OpenPortAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SelectedPort))
                {
                    StatusMessage = "请先选择串口";
                    return;
                }

                if (string.Equals(SelectedPort, "TEST-OPEN-EX", StringComparison.OrdinalIgnoreCase) || BaudRate == 999001)
                {
                    throw new InvalidOperationException("打开串口异常测试：用于验证异常日志与指标上报链路");
                }

                if (PollIntervalMs < 0)
                {
                    PollIntervalMs = 0;
                }

                if (PollIntervalMs < MinPollIntervalMs)
                {
                    PollIntervalMs = MinPollIntervalMs;
                    _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] WARN: 轮询间隔过小，已自动调整为 {MinPollIntervalMs}ms");
                }

                if (!ValidateCommandParameters(out var validationMessage))
                {
                    StatusMessage = validationMessage;
                    return;
                }

                var result = _serialPortService.OpenPort(SelectedPort, BaudRate, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusRTU);
                StatusMessage = result.Message ?? string.Empty;

                if (!result.IsSuccess)
                {
                    return;
                }

                if (_serialPortService.TryGetContext(SelectedPort, out var ctx) && ctx is IModbusContext modbus)
                {
                    _modbusContext = modbus;
                    IsPortOpened = true;
                    UpdateStatusMessage();
                    return;
                }

                await _serialPortService.ClosePortAsync(SelectedPort);
                StatusMessage = "串口上下文不是 Modbus 上下文";
            }
            catch (Exception ex)
            {
                _logger.AddLog(LogLevel.Error, "打开串口失败，Port={Port}", exception: ex, args: SelectedPort ?? "<null>");
                StatusMessage = $"打开串口失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 停止采集并关闭所选串口。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanClosePort))]
        private async Task ClosePortAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedPort))
            {
                return;
            }

            if (IsAcquiring)
            {
                await StopAcquisitionAsync();
            }

            var result = await _serialPortService.ClosePortAsync(SelectedPort);
            if (result.IsSuccess)
            {
                _modbusContext = null;
                IsPortOpened = false;
            }
            StatusMessage = result.Message ?? string.Empty;
        }

        [RelayCommand(CanExecute = nameof(CanStartAcquisition))]
        private Task StartAcquisitionAsync()
        {
            if (!IsPortOpened)
            {
                StatusMessage = "请先打开串口";
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(SelectedPort) || _modbusContext == null)
            {
                StatusMessage = "串口上下文不可用";
                return Task.CompletedTask;
            }

            if (PollIntervalMs < 0)
            {
                PollIntervalMs = 0;
            }

            if (PollIntervalMs < MinPollIntervalMs)
            {
                PollIntervalMs = MinPollIntervalMs;
                _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] WARN: 轮询间隔过小，已自动调整为 {MinPollIntervalMs}ms");
            }

            if (!ValidateCommandParameters(out var validationMessage))
            {
                StatusMessage = validationMessage;
                return Task.CompletedTask;
            }

            Interlocked.Exchange(ref _segmentSendCount, 0);
            Interlocked.Exchange(ref _segmentReceiveCount, 0);
            _segmentIndex++;

            _acquisitionCts = new CancellationTokenSource();
            _persistQueue = Channel.CreateBounded<ModbusPacket>(new BoundedChannelOptions(8192)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

            _persistWorkerTasks = Enumerable.Range(0, PersistWorkerCount)
                .Select(_ => Task.Run(() => PersistWorkerLoopAsync(_acquisitionCts.Token)))
                .ToArray();

            _sendLoopTask = Task.Run(() => SendLoopAsync(_modbusContext, _acquisitionCts.Token));
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_modbusContext, _acquisitionCts.Token));
            _diagnosticTask = Task.Run(() => DiagnosticLoopAsync(_acquisitionCts.Token));

            IsAcquiring = true;
            _uiLines.Writer.TryWrite($"===== 第{_segmentIndex}段开始 =====");
            UpdateStatusMessage();
            return Task.CompletedTask;
        }

        [RelayCommand(CanExecute = nameof(CanStopAcquisition))]
        private async Task StopAcquisitionAsync()
        {
            if (!IsAcquiring)
            {
                return;
            }

            if (_acquisitionCts != null)
            {
                _acquisitionCts.Cancel();
            }

            if (_persistQueue != null)
            {
                _persistQueue.Writer.TryComplete();
            }

            if (_sendLoopTask != null || _receiveLoopTask != null || _diagnosticTask != null || (_persistWorkerTasks?.Length > 0))
            {
                var tasks = new List<Task>();
                if (_sendLoopTask != null) tasks.Add(_sendLoopTask);
                if (_receiveLoopTask != null) tasks.Add(_receiveLoopTask);
                if (_diagnosticTask != null) tasks.Add(_diagnosticTask);
                if (_persistWorkerTasks != null) tasks.AddRange(_persistWorkerTasks);

                try
                {
                    // 步骤1：等待发送任务有序退出，并设置超时上限。
                    // 为什么：避免串口异常场景下关闭流程无限等待。
                    // 风险点：无超时兜底会导致停止采集或关窗卡死。
                    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                    _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] STOP-WARN: 等待采集任务退出超时，继续执行关闭流程");
                }
                catch
                {
                }
            }

            _acquisitionCts?.Dispose();
            _acquisitionCts = null;
            _sendLoopTask = null;
            _receiveLoopTask = null;
            _diagnosticTask = null;
            _persistWorkerTasks = null;
            _persistQueue = null;
            IsAcquiring = false;

            _uiLines.Writer.TryWrite($"===== 第{_segmentIndex}段结束，发送={Interlocked.Read(ref _segmentSendCount)}，接收={Interlocked.Read(ref _segmentReceiveCount)} =====");
            UpdateStatusMessage();
        }

        /// <summary>
        /// 清空接收区显示内容。
        /// </summary>
        [RelayCommand]
        private void ClearReceivedData()
        {
            _receivedDataBuffer.Clear();
            ReceivedData = string.Empty;
        }

        /// <summary>
        /// 窗口关闭前执行清理，确保串口和事件订阅正确释放。
        /// </summary>
        public async Task CleanupAsync()
        {
            // 步骤1：优先停止采集任务。
            // 为什么：先停发送循环可降低关窗阶段的并发冲突。
            // 风险点：采集未停就关串口，可能触发发送异常或等待卡顿。
            if (IsAcquiring)
            {
                await StopAcquisitionAsync();
            }

            // 步骤2：关闭已打开串口。
            // 为什么：释放串口句柄，避免下次启动占用失败。
            // 风险点：不关闭串口会导致端口残留占用。
            if (IsPortOpened)
            {
                await ClosePortAsync();
            }

            // 步骤3：停止 UI 刷新计时器并解绑事件。
            // 为什么：防止窗口销毁后仍触发 UI 回调。
            // 风险点：未解绑事件可能导致内存泄漏或对象已释放异常。
            _uiFlushTimer.Stop();
            _uiFlushTimer.Tick -= OnUiFlushTimerTick;
        }

        /// <summary>
        /// 同步轮询发送线程：每次请求等待匹配响应后再发下一次。
        /// </summary>
        private async Task SendLoopAsync(IModbusContext modbus, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 步骤1：记录本轮开始时间。
                // 为什么：用于实现“任意 ms 配置”的精确轮询节奏。
                // 风险点：仅使用固定 Delay 会叠加处理时间，实际周期会漂移。
                var cycleWatch = Stopwatch.StartNew();

                // 步骤2：构建并发送读取命令。
                // 为什么：统一走 Modbus 请求-响应闭环，避免高频盲发。
                // 风险点：若并行发送或不等响应，易出现回包串台与丢包。
                var command = BuildReadCommand();
                var txHex = BitConverter.ToString(command);
                Interlocked.Increment(ref _totalSendCount);
                Interlocked.Increment(ref _segmentSendCount);
                _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] TX: {txHex}");
                _logger.AddLog(LogLevel.Information, "发送命令: {Command}", args: new object[] { txHex });
                try
                {
                    // 步骤3：等待匹配响应（同步轮询核心）。
                    // 为什么：确保一问一答，杜绝 20ms 高频下的“只发不收”。
                    // 风险点：超时参数过小会频繁超时，过大则降低吞吐。
                    var packet = await modbus.SendRequestAsync(command, timeout: 3000, retryCount: 1, cancellationToken).ConfigureAwait(false);
                    var index = Interlocked.Increment(ref _totalReceiveCount);
                    Interlocked.Increment(ref _segmentReceiveCount);
                    _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] #{index} RX: {BitConverter.ToString(packet.RawFrame)}");
                    _logger.AddLog(LogLevel.Information, "接收响应: {Response}", args: new object[] { BitConverter.ToString(packet.RawFrame) });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] RX-ERR: {ex.Message}");
                    _logger.AddLog(LogLevel.Error, "发送循环接收响应失败，Command={Command}", exception: ex, args: txHex);
                }

                // 步骤4：按配置补齐剩余周期，支持任意 ms（含 0ms）。
                // 为什么：当响应快于周期时保持固定节奏；响应慢于周期时立即下一轮。
                // 风险点：不扣除处理耗时会导致实际周期 > 配置值。
                cycleWatch.Stop();
                var remainingMs = PollIntervalMs - (int)cycleWatch.ElapsedMilliseconds;
                if (remainingMs > 0)
                {
                    await Task.Delay(remainingMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void UpdateStatusMessage()
        {
            if (!IsPortOpened)
            {
                StatusMessage = "未连接";
                return;
            }

            var totalSend = Interlocked.Read(ref _totalSendCount);
            var totalReceive = Interlocked.Read(ref _totalReceiveCount);
            var segmentSend = Interlocked.Read(ref _segmentSendCount);
            var segmentReceive = Interlocked.Read(ref _segmentReceiveCount);
            var segmentText = _segmentIndex > 0 ? $"第{_segmentIndex}段" : "未开始";
            var runningText = IsAcquiring ? "采集中" : "已停止";

            StatusMessage = $"{segmentText}{runningText} | 总发送={totalSend} 总接收={totalReceive} | 本段发送={segmentSend} 本段接收={segmentReceive}";
        }

        private bool ValidateCommandParameters(out string message)
        {
            if (SlaveId is < 1 or > 247)
            {
                message = "从站地址范围应为 1-247";
                return false;
            }

            if (FunctionCode is < 1 or > 127)
            {
                message = "功能码范围应为 1-127";
                return false;
            }

            if (StartAddress is < 0 or > 65535)
            {
                message = "寄存器起始地址范围应为 0-65535";
                return false;
            }

            if (RegisterCount is < 1 or > 125)
            {
                message = "寄存器长度范围应为 1-125";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private byte[] BuildReadCommand()
        {
            var cmd = new List<byte>
            {
                (byte)SlaveId,
                (byte)FunctionCode,
                (byte)(StartAddress >> 8),
                (byte)(StartAddress & 0xFF),
                (byte)(RegisterCount >> 8),
                (byte)(RegisterCount & 0xFF)
            };

            return AddCrcHelpers.AddCRC(cmd);
        }

        private void OnUiFlushTimerTick(object? sender, EventArgs e)
        {
            if (!_uiLines.Reader.TryPeek(out _))
            {
                UpdateStatusMessage();
                return;
            }

            var sb = new StringBuilder();
            while (_uiLines.Reader.TryRead(out var line))
            {
                sb.AppendLine(line);
            }

            if (sb.Length > 0)
            {
                _receivedDataBuffer.Append(sb);
            }

            const int maxChars = 120_000;
            if (_receivedDataBuffer.Length > maxChars)
            {
                _receivedDataBuffer.Remove(0, _receivedDataBuffer.Length - maxChars);
            }

            ReceivedData = _receivedDataBuffer.ToString();

            UpdateStatusMessage();
        }
    }
}
