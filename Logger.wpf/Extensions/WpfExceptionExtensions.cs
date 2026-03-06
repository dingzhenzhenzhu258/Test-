using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Logger.wpf.Extensions
{
    /// <summary>
    /// WPF 全局异常订阅扩展。
    /// 用于统一捕获 UI 线程、后台线程与未观察 Task 异常，
    /// 并通过自定义 Logger 输出到日志系统。
    /// </summary>
    public static class WpfExceptionExtensions
    {
        /// <summary>
        /// 订阅 WPF 全局未处理异常。
        /// 建议在 <c>App.OnStartup</c> 完成日志初始化后立即调用。
        /// </summary>
        /// <param name="app">WPF 应用对象</param>
        /// <param name="logger">统一日志实例</param>
        public static void SubscribeGlobalExceptions(this Application app, Microsoft.Extensions.Logging.ILogger logger)
        {
            // 1. 捕获 UI 线程的未处理异常
            app.DispatcherUnhandledException += (sender, e) =>
            {
                // 【修改这里】调用你封装的基础类库方法！还可以顺便让它弹窗或显示在 UI 列表里
                logger.AddLog(LogLevel.Critical, "WPF UI 线程发生未处理致命异常！", isShowUI: true, exception: e.Exception);

                // 【保持不变】这是确保闪退前，把这最后一条致命日志发给 OpenObserve 的唯一方式
                Log.CloseAndFlush();
            };

            // 2. 捕获后台线程的未处理异常
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    logger.AddLog(LogLevel.Critical, "WPF 后台线程发生未处理致命异常！", isShowUI: true, exception: ex);
                }
                Log.CloseAndFlush();
            };

            // 3. 捕获 Task 中未观察到的异常 (GC 回收时触发)
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                logger.AddLog(LogLevel.Error, "WPF 发生未观察到的 Task 异常！", isShowUI: true, exception: e.Exception);
                e.SetObserved(); // 标记为已观察，阻止程序崩溃
            };
        }
    }
}
