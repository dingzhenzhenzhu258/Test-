using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Text;
using System.Windows.Threading;
using Test_High_speed_acquisition.Services.Parser;
using Test_High_speed_acquisition.ViewModels.Models;

namespace Test_High_speed_acquisition.ViewModels.Windows
{
    public partial class MainWindowViewModel : ViewModel
    {
        private readonly ISerialPortService _serialPortService;
        private readonly Dispatcher _dispatcher;
        private IPortContext? _currentContext;
        private readonly RawDataParser _rawDataParser = new();

        public MainWindowViewModel(ISerialPortService serialPortService, Dispatcher dispatcher)
        {
            _serialPortService = serialPortService;
            _dispatcher = dispatcher;

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
        [NotifyCanExecuteChangedFor(nameof(OpenPortCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClosePortCommand))]
        private bool isPortOpened;

        [ObservableProperty]
        private string receivedData = string.Empty;

        [ObservableProperty]
        private string statusMessage = "未连接";

        private bool CanOpenPort() => !IsPortOpened && !string.IsNullOrWhiteSpace(SelectedPort);

        private bool CanClosePort() => IsPortOpened && !string.IsNullOrWhiteSpace(SelectedPort);

        [RelayCommand]
        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(x => x).ToArray();

            AvailablePorts.Clear();
            foreach (var port in ports)
            {
                AvailablePorts.Add(port);
            }

            if (!string.IsNullOrWhiteSpace(SelectedPort) && AvailablePorts.Contains(SelectedPort))
            {
                return;
            }

            SelectedPort = AvailablePorts.FirstOrDefault();
        }

        [RelayCommand(CanExecute = nameof(CanOpenPort))]
        private void OpenPort()
        {
            if (string.IsNullOrWhiteSpace(SelectedPort))
            {
                StatusMessage = "请先选择串口";
                return;
            }

            var result = _serialPortService.OpenPort(SelectedPort, BaudRate, Parity.None, 8, StopBits.One, _rawDataParser);
            StatusMessage = result.Message ?? string.Empty;

            if (!result.IsSuccess)
            {
                return;
            }

            if (_serialPortService.TryGetContext(SelectedPort, out var context) && context != null)
            {
                _currentContext = context;
                _currentContext.OnHandleChanged += OnSerialDataReceived;
            }

            IsPortOpened = true;
        }

        [RelayCommand(CanExecute = nameof(CanClosePort))]
        private void ClosePort()
        {
            if (string.IsNullOrWhiteSpace(SelectedPort))
            {
                return;
            }

            UnsubscribeCurrentContext();
            var result = _serialPortService.ClosePort(SelectedPort);
            StatusMessage = result.Message ?? string.Empty;
            IsPortOpened = false;
        }

        [RelayCommand]
        private void ClearReceivedData()
        {
            ReceivedData = string.Empty;
        }

        public void Cleanup()
        {
            if (IsPortOpened)
            {
                ClosePort();
            }
            else
            {
                UnsubscribeCurrentContext();
            }
        }

        private void UnsubscribeCurrentContext()
        {
            if (_currentContext is null)
            {
                return;
            }

            _currentContext.OnHandleChanged -= OnSerialDataReceived;
            _currentContext = null;
        }

        private async void OnSerialDataReceived(object? sender, object e)
        {
            if (e is not OperateResult<byte[]> result || result.Content is null || result.Content.Length == 0)
            {
                return;
            }

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {BitConverter.ToString(result.Content)}{Environment.NewLine}";
            await _dispatcher.InvokeAsync(() =>
            {
                ReceivedData += line;
            });
        }
    }
}
