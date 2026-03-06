using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Exceptions;
using Serilog.Sinks.OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Debugging;

namespace Logger.Extensions
{
    /// <summary>
    /// Logger 统一接入扩展。
    /// 提供配置初始化、Serilog + OpenTelemetry 管道注册、
    /// 以及自定义追踪 Activity 的统一入口。
    /// </summary>
    public static class LoggerExtensions
    {
        private static int _selfLogHooked;
        private static int _otlpAlertRaised;

        /// <summary>
        /// 确保 appsettings.json 配置文件存在。
        /// 如果不存在，则从 Logger 库的嵌入资源中释放默认配置文件。
        /// </summary>
        public static void EnsureConfigInitialized()
        {
            EnsureConfigInitialized(null, "appsettings.json", msg => Console.WriteLine(msg));
        }

        /// <summary>
        /// 确保指定配置文件存在。
        /// 若文件缺失，则从当前程序集的嵌入资源释放到目标目录。
        /// </summary>
        /// <param name="basePath">目标目录，为空时使用应用基目录</param>
        /// <param name="fileName">配置文件名（例如 appsettings.json）</param>
        /// <param name="onMessage">过程消息回调（可选）</param>
        /// <returns>创建了新文件返回 true；文件已存在或失败返回 false</returns>
        public static bool EnsureConfigInitialized(string? basePath, string fileName, Action<string>? onMessage = null)
        {
            basePath ??= AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(basePath, fileName);

            if (File.Exists(configPath))
            {
                return false;
            }

            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var assembly = typeof(LoggerExtensions).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                onMessage?.Invoke($"[Logger] 未找到嵌入资源: {fileName}");
                return false;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                onMessage?.Invoke($"[Logger] 无法读取嵌入资源: {resourceName}");
                return false;
            }

            using var fileStream = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.CopyTo(fileStream);
            onMessage?.Invoke($"[Logger] 已自动创建默认配置文件: {configPath}");
            return true;
        }

        // ==========================================
        // 【新增】定义一个专门用于自定义耗时追踪的 Source
        // ==========================================
        public static readonly ActivitySource TraceSource = new ActivitySource("Hardware.Tracer");

        /// <summary>
        /// 开启一段耗时追踪。强烈建议配合 using 语法使用。
        /// </summary>
        /// <param name="operationName">追踪的操作名称（例如："读取 PLC 寄存器"）</param>
        public static Activity? StartTrace(
            string operationName,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFilePath = "")
        {
            // 开启一个新的 Span (追踪片段)
            var activity = TraceSource.StartActivity(operationName);

            // 自动为你打上标签：记录是哪个方法、哪个文件发起的耗时操作
            activity?.SetTag("code.function", memberName);
            activity?.SetTag("code.filepath", System.IO.Path.GetFileName(sourceFilePath));

            return activity;
        }

        /// <summary>
        /// 注册统一日志/追踪/指标管道。
        /// </summary>
        /// <param name="services">DI 容器</param>
        /// <param name="configuration">应用配置</param>
        /// <param name="projectName">服务名称（映射到 service.name，必须唯一且非默认值）</param>
        /// <param name="isWebApi">是否启用 AspNetCore 相关自动采集</param>
        public static IServiceCollection AddSerilogLogging(
            this IServiceCollection services,
            IConfiguration configuration,
            string projectName = "xxxapi",
            bool isWebApi = false)
        {
            if (string.IsNullOrWhiteSpace(projectName) || string.Equals(projectName, "xxxapi", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("projectName 必须为每个服务设置唯一且非默认值（不能为 xxxapi）。", nameof(projectName));
            }

            HookOtlpSelfLogOnce();

            // 1. 准备认证信息 (保留了你原本的账号密码组合) 
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin@example.com:Kb123456@")); 

            // 2. 创建统一的资源标签 (Logs 和 Traces 共享)
            var resourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = projectName,
                ["environment"] = "development", 
                ["service.version"] = "1.0.0" 
            };

            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(projectName) 
                .AddAttributes(resourceAttributes);

            // ==========================================
            // 模块 A: 配置 Serilog (全权负责处理 Logs)
            // ==========================================
            var loggerConfig = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                // 【关键新增】挂载全局动态开关
                .MinimumLevel.ControlledBy(LoggerLevelManager.LogSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                // ==========================================
                // 【新增】全局静态属性注入（彻底焊死，无视线程切换）
                .Enrich.WithProperty("MachineName", GlobalDeviceInfo.MachineName)
                .Enrich.WithProperty("AppVersion", GlobalDeviceInfo.AppVersion)
                .Enrich.WithProperty("IPAddress", GlobalDeviceInfo.IpAddress)
                .Enrich.WithProperty("MACAddress", GlobalDeviceInfo.MacAddress)
                // ==========================================
                .WriteTo.Console()
                .WriteTo.File(
                    path: Path.Combine("logs", "fallback.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                // 将日志推送到 OpenObserve (完全替代了原先错误的 builder.Logging.AddOpenTelemetry)
                .WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = "http://localhost:5080/api/default/v1/logs"; // 
                    options.Protocol = OtlpProtocol.HttpProtobuf; // 
                    options.Headers = new Dictionary<string, string> { ["Authorization"] = $"Basic {credentials}" }; // 
                    options.ResourceAttributes = resourceAttributes;

                    // 完美还原你的高频推送配置
                    options.BatchingOptions.BatchSizeLimit = 10; // 
                    options.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(500); // 
                });

            Log.Logger = loggerConfig.CreateLogger();

            // 替换主程序的日志管道为 Serilog
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(Log.Logger, dispose: true);
            });

            // 【新增】显式设置全局传播器为 W3C 标准（确保跨端 ID 一致）
            Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

            // 模块 B: 配置原生 OpenTelemetry (处理 Traces 和 Metrics)
            // ==========================================
            services.AddOpenTelemetry()
                    .WithTracing(tracingBuilder =>
                    {
                        tracingBuilder.SetResourceBuilder(resourceBuilder)
                        .AddSource(projectName) // 注册当前项目为追踪源
                        .AddSource("Hardware.Tracer") // 【极其关键的新增】告诉系统监听我们刚刚写的自定义追踪源！
                        .AddHttpClientInstrumentation(); // WPF 和 API 都会往外发请求，所以都需要


                        // 根据传入的标志，决定是否监听进来的 HTTP 请求
                        if (isWebApi)
                        {
                            tracingBuilder.AddAspNetCoreInstrumentation();
                        }

                        tracingBuilder.AddOtlpExporter(opts =>
                        {
                            opts.Endpoint = new Uri("http://localhost:5080/api/default/v1/traces");
                            opts.Headers = $"Authorization=Basic {credentials}";
                            opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                        });
                    })
                    .WithMetrics(metricsBuilder =>
                    {
                        metricsBuilder.SetResourceBuilder(resourceBuilder)
                            .AddRuntimeInstrumentation() // 收集 CPU 内存
                            .AddHttpClientInstrumentation()
                            .AddMeter("SerialPortService.GenericHandler")
                            .AddMeter(projectName);

                        if (isWebApi)
                        {
                            metricsBuilder.AddAspNetCoreInstrumentation(); // 收集 API 的 QPS 指标
                        }

                        metricsBuilder.AddOtlpExporter(opts =>
                        {
                            opts.Endpoint = new Uri("http://localhost:5080/api/default/v1/metrics");
                            opts.Headers = $"Authorization=Basic {credentials}";
                            opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                        });
                    });

            // 注意：不要在专门配日志的扩展方法里注册 Controllers 或 HttpClient。
            // 那些应该由主程序的 Program.cs 自己去注册。

            return services; 
        }

        /// <summary>
        /// 绑定 Serilog SelfLog，捕获 OTLP 导出异常并触发一次性告警。
        /// </summary>
        private static void HookOtlpSelfLogOnce()
        {
            if (Interlocked.CompareExchange(ref _selfLogHooked, 1, 0) != 0)
            {
                return;
            }

            SelfLog.Enable(message =>
            {
                if (Interlocked.CompareExchange(ref _otlpAlertRaised, 1, 0) != 0)
                {
                    return;
                }

                var lower = message?.ToLowerInvariant() ?? string.Empty;
                if (!lower.Contains("opentelemetry") && !lower.Contains("otlp") && !lower.Contains("5080"))
                {
                    Interlocked.Exchange(ref _otlpAlertRaised, 0);
                    return;
                }

                var alert = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Logger] OTLP 导出异常，已自动回退到文件日志（logs/fallback.log）。原始消息: {message}{Environment.NewLine}";
                try
                {
                    var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "fallback.log"), alert, Encoding.UTF8);
                }
                catch
                {
                }

                try
                {
                    Console.Error.WriteLine(alert);
                }
                catch
                {
                }
            });
        }
    }
}
