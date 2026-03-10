using CommunityToolkit.Mvvm.Messaging;
using Logger.Extensions;
using Logger.wpf.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SerialPortService.Extensions;
using System.IO;
using System.Windows;
using Test_High_speed_acquisition.Services;
using Test_High_speed_acquisition.ViewModels.Windows;
using Test_High_speed_acquisition.Views.Windows;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;
using LoggerExtensions = Logger.Extensions.LoggerExtensions;

namespace Test_High_speed_acquisition
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public new static App Current => (App)Application.Current;

        // The.NET Generic Host provides dependency injection, configuration, logging, and other services.
        // https://docs.microsoft.com/dotnet/core/extensions/generic-host
        // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
        // https://docs.microsoft.com/dotnet/core/extensions/configuration
        // https://docs.microsoft.com/dotnet/core/extensions/logging
        private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration(c =>
        {
            var basePath =
                Path.GetDirectoryName(AppContext.BaseDirectory)
                ?? throw new DirectoryNotFoundException(
                    "Unable to find the base directory of the application."
                );

            LoggerExtensions.EnsureConfigInitialized(basePath, "appsettings.json");
            _ = c.SetBasePath(basePath).AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        })
        .ConfigureServices(
            (context, services) =>
            {
                // Logging
                services.AddSerilogLogging(context.Configuration, projectName: "Test-High-speed");

                // App Host
                services.AddHostedService<ApplicationHostService>();
                services.AddHostedService<UnimplementedUserService>();

                // Messenger
                services.AddSingleton<WeakReferenceMessenger>();
                services.AddSingleton<IMessenger, WeakReferenceMessenger>(provider => provider.GetRequiredService<WeakReferenceMessenger>());

                // Wpf.Ui Services
                // 注册页面解析器，支持 NavigationView 按页面类型导航
                services.AddNavigationViewPageProvider();
                // 主题服务
                services.AddSingleton<IThemeService, ThemeService>();
                // 任务栏服务
                services.AddSingleton<ITaskBarService, TaskBarService>();
                // 导航服务
                services.AddSingleton<INavigationService, NavigationService>();
                // Snackbar 服务
                services.AddSingleton<ISnackbarService, SnackbarService>();
                // Dialog 服务
                services.AddSingleton<IContentDialogService, ContentDialogService>();

                // 注册 SerialPortService 以便在应用程序中使用串口通信功能
                services.AddSerialPortService(context.Configuration);

                // 注册 Dispatcher 以便在 ViewModel 中使用它来执行 UI 线程上的操作
                services.AddSingleton(_ => Current.Dispatcher);

                services.AddSingleton<MainWindow>(sp => new MainWindow { DataContext = sp.GetRequiredService<MainWindowViewModel>() });
                services.AddSingleton<MainWindowViewModel>();
            }
        )
        .Build();

        /// <summary>
        /// Gets services.
        /// </summary>
        public static IServiceProvider Services
        {
            get { return _host.Services; }
        }

        /// <summary>
        /// Occurs when the application is loading.
        /// </summary>
        private async void OnStartup(object sender, StartupEventArgs e)
        {
            await _host.StartAsync();

            var logger = Services.GetRequiredService<ILogger<App>>();
            Current.SubscribeGlobalExceptions(logger);
        }

        /// <summary>
        /// Occurs when the application is closing.
        /// </summary>
        private async void OnExit(object sender, ExitEventArgs e)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
