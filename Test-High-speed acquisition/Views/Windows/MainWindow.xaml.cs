using System;
using System.Windows;
using System.Windows.Controls;
using Test_High_speed_acquisition.ViewModels.Windows;

namespace Test_High_speed_acquisition.Views.Windows
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Closed += OnClosedAsync;
        }

        private async void OnClosedAsync(object? sender, EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // 步骤1：异步执行清理，避免 UI 线程同步阻塞。
                // 为什么：关窗阶段若同步等待后台任务，容易出现卡死。
                // 风险点：不等待清理会遗留串口占用与计时器回调。
                await vm.CleanupAsync();
            }
        }

        private void ReceivedDataTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.ScrollToEnd();
            }
        }
    }
}
