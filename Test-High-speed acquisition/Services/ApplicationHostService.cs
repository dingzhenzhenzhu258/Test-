using CommunityToolkit.Mvvm.Messaging;
using Logger.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Test_High_speed_acquisition.Views.Windows;
using Wpf.Ui;

namespace Test_High_speed_acquisition.Services
{
    /// <summary>
    /// 应用程序宿主服务。
    /// 负责在宿主启动后执行应用激活流程。
    /// </summary>
    public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
    {
        /// <summary>
        /// 当宿主准备启动该服务时触发
        /// 容器已经构建完成后的运行阶段
        /// </summary>
        /// <param name="cancellationToken">用于指示启动流程是否被取消。</param>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await HandleActivationAsync();
        }

        /// <summary>
        /// 当宿主执行优雅关闭时触发。
        /// </summary>
        /// <param name="cancellationToken">用于指示关闭流程是否应被中断。</param>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// 在应用激活阶段创建并显示主窗口。
        /// </summary>
        private async Task HandleActivationAsync()
        {
            await Task.CompletedTask;

            if (!Application.Current.Windows.OfType<MainWindow>().Any())
            {
                App.Current.MainWindow = serviceProvider.GetRequiredService<MainWindow>();
                App.Current.MainWindow.Visibility = Visibility.Visible;
            }

            await Task.CompletedTask;
        }
    }
}
