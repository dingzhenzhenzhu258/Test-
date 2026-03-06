using System;
using System.Windows;
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
            Closed += OnClosed;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Cleanup();
            }
        }
    }
}
