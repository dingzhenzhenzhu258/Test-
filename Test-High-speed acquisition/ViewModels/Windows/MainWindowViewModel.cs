using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialPortService.Helpers;
using SerialPortService.Models.Emuns;
using SerialPortService.Services.Interfaces;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using Test_High_speed_acquisition.ViewModels.Models;

namespace Test_High_speed_acquisition.ViewModels.Windows
{
    /// <summary>
    /// 主窗口视图模型：负责串口枚举、打开/关闭串口、接收数据展示等交互逻辑。
    /// </summary>
    public partial class MainWindowViewModel : ViewModel
    {
        private readonly ISerialPortService _serialPortService;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _acquisitionCts;
        private Task? _sendLoopTask;
        private Task? _receiveLoopTask;
        private IModbusContext? _modbusContext;
        private readonly Channel<string> _uiLines;
        private readonly DispatcherTimer _uiFlushTimer;
        private long _totalSendCount;
        private long _totalReceiveCount;
        private long _segmentSendCount;
        private long _segmentReceiveCount;
        private int _segmentIndex;

        public MainWindowViewModel(ISerialPortService serialPortService, Dispatcher dispatcher)
        {
            _serialPortService = serialPortService;
            _dispatcher = dispatcher;

            _uiLines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = true
            });

            _uiFlushTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _uiFlushTimer.Tick += OnUiFlushTimerTick;
            _uiFlushTimer.Start();

            RefreshPorts();
            if (AvailablePorts.Count > 0)
            {
                SelectedPort = AvailablePorts[0];
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
            if (string.IsNullOrWhiteSpace(SelectedPort))
            {
                StatusMessage = "请先选择串口";
                return;
            }

            if (PollIntervalMs < 0)
            {
                PollIntervalMs = 0;
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

            _serialPortService.ClosePort(SelectedPort);
            StatusMessage = "串口上下文不是 Modbus 上下文";
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

            var result = _serialPortService.ClosePort(SelectedPort);
            _modbusContext = null;
            IsPortOpened = false;
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

            if (!ValidateCommandParameters(out var validationMessage))
            {
                StatusMessage = validationMessage;
                return Task.CompletedTask;
            }

            Interlocked.Exchange(ref _segmentSendCount, 0);
            Interlocked.Exchange(ref _segmentReceiveCount, 0);
            _segmentIndex++;

            _acquisitionCts = new CancellationTokenSource();
            _sendLoopTask = Task.Run(() => SendLoopAsync(SelectedPort, _acquisitionCts.Token));
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_modbusContext, _acquisitionCts.Token));

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

            if (_sendLoopTask != null || _receiveLoopTask != null)
            {
                var tasks = new[] { _sendLoopTask, _receiveLoopTask }
                    .Where(t => t != null)
                    .Cast<Task>()
                    .ToArray();

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }
            }

            _acquisitionCts?.Dispose();
            _acquisitionCts = null;
            _sendLoopTask = null;
            _receiveLoopTask = null;
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
            ReceivedData = string.Empty;
        }

        /// <summary>
        /// 窗口关闭前执行清理，确保串口和事件订阅正确释放。
        /// </summary>
        public void Cleanup()
        {
            if (IsAcquiring)
            {
                StopAcquisitionAsync().GetAwaiter().GetResult();
            }

            if (IsPortOpened)
            {
                ClosePortAsync().GetAwaiter().GetResult();
            }

            _uiFlushTimer.Stop();
            _uiFlushTimer.Tick -= OnUiFlushTimerTick;
        }

        /// <summary>
        /// 高速发送线程：持续发送 Modbus 读命令。
        /// </summary>
        private async Task SendLoopAsync(string portName, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var command = BuildReadCommand();
                await _serialPortService.Write(portName, command).ConfigureAwait(false);
                Interlocked.Increment(ref _totalSendCount);
                Interlocked.Increment(ref _segmentSendCount);
                _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] TX: {BitConverter.ToString(command)}");

                if (PollIntervalMs > 0)
                {
                    await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 高速接收线程：持续读取已解析的 Modbus 报文。
        /// </summary>
        private async Task ReceiveLoopAsync(IModbusContext modbus, CancellationToken cancellationToken)
        {
            await foreach (var packet in modbus.ReadParsedPacketsAsync(cancellationToken).ConfigureAwait(false))
            {
                var index = Interlocked.Increment(ref _totalReceiveCount);
                Interlocked.Increment(ref _segmentReceiveCount);
                _uiLines.Writer.TryWrite($"[{DateTime.Now:HH:mm:ss.fff}] #{index} RX: {BitConverter.ToString(packet.RawFrame)}");
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
            var count = 0;
            while (count < 200 && _uiLines.Reader.TryRead(out var line))
            {
                sb.AppendLine(line);
                count++;
            }

            ReceivedData += sb.ToString();

            const int maxChars = 120_000;
            if (ReceivedData.Length > maxChars)
            {
                ReceivedData = ReceivedData[^maxChars..];
            }

            UpdateStatusMessage();
        }
    }
}
