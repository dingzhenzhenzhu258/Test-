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
using System.Net.Sockets;
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
        /// <summary>
        /// 标记 SelfLog 是否已完成一次性挂载。
        /// 0 表示未挂载，1 表示已挂载。
        /// </summary>
        private static int _selfLogHooked;

        /// <summary>
        /// 标记 OTLP 异常告警是否已触发。
        /// 用于抑制重复告警风暴。
        /// </summary>
        private static int _otlpAlertRaised;

        /// <summary>
        /// SelfLog 关键字过滤集合。
        /// 仅命中关键字的消息才会进入 OTLP 异常告警流程。
        /// </summary>
        private static string[] _otlpSelfLogKeywords = new[] { "opentelemetry", "otlp", "5080" };

        /// <summary>
        /// 标记恢复探测告警是否已输出。
        /// 0 表示未通知，1 表示已通知。
        /// </summary>
        private static int _otlpRecoveryNotified;

        /// <summary>
        /// OTLP 恢复探测定时器。
        /// </summary>
        private static Timer? _otlpRecoveryTimer;

        /// <summary>
        /// 保护恢复探测定时器生命周期的互斥锁。
        /// </summary>
        private static readonly object otlpRecoveryTimerLock = new();

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

            // 步骤1：统一读取 OTLP 端点并决定是否启用远端导出。
            // 为什么：当日志服务器不可达时可在启动期直接降级，减少无效重试开销。
            // 风险点：若端点配置错误且仍强制导出，会持续消耗线程/网络资源。
            var otlpLogsEndpoint = configuration["Logger:Otlp:LogsEndpoint"] ?? "http://localhost:5080/api/default/v1/logs";
            var otlpTracesEndpoint = configuration["Logger:Otlp:TracesEndpoint"] ?? "http://localhost:5080/api/default/v1/traces";
            var otlpMetricsEndpoint = configuration["Logger:Otlp:MetricsEndpoint"] ?? "http://localhost:5080/api/default/v1/metrics";

            // 步骤1.1：刷新 SelfLog 关键字，兼容自定义 OTLP 端点。
            // 为什么：SelfLog 告警过滤不能硬编码 5080，需要跟随配置变化。
            // 风险点：关键字未更新会导致真实导出错误被误判为无关日志。
            UpdateOtlpSelfLogKeywords(otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);

            // 步骤1.2：统一计算 Telemetry 导出器启用策略。
            // 为什么：Tracing/Metrics 可在端点恢复后自动继续上报，无需额外重建。
            // 风险点：若关闭该策略，Telemetry 需重启应用后才会恢复导出。
            var otlpEnabled = ShouldEnableOtlp(configuration, out var otlpDisabledReason, otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);
            var autoRecoverTelemetry = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForTelemetry") ?? true;
            var enableTelemetryExporters = otlpEnabled || autoRecoverTelemetry;

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
            var loggerConfig = CreateLoggerConfiguration(configuration, resourceAttributes, credentials, otlpLogsEndpoint, otlpEnabled);

            if (!otlpEnabled)
            {
                WriteOtlpFallbackNotice($"启动时已禁用 OTLP 导出，仅保留本地日志。原因: {otlpDisabledReason}");

                if (autoRecoverTelemetry)
                {
                    WriteOtlpFallbackNotice("Tracing/Metrics 导出器已保持启用状态，待 OTLP 端点恢复后将自动恢复上报。");
                }

                // 步骤3：启动后台恢复探测与日志热恢复。
                // 为什么：服务恢复后可自动恢复日志上报，减少人工干预。
                // 风险点：若日志热恢复失败，将继续停留在本地日志兜底模式。
                StartOtlpRecoveryMonitor(configuration, credentials, resourceAttributes, otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);
            }

            Log.Logger = loggerConfig.CreateLogger();

            // 替换主程序的日志管道为 Serilog
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
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

                        if (enableTelemetryExporters)
                        {
                            tracingBuilder.AddOtlpExporter(opts =>
                            {
                                opts.Endpoint = new Uri(otlpTracesEndpoint);
                                opts.Headers = $"Authorization=Basic {credentials}";
                                opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                            });
                        }
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

                        if (enableTelemetryExporters)
                        {
                            metricsBuilder.AddOtlpExporter(opts =>
                            {
                                opts.Endpoint = new Uri(otlpMetricsEndpoint);
                                opts.Headers = $"Authorization=Basic {credentials}";
                                opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                            });
                        }
                    });

            // 注意：不要在专门配日志的扩展方法里注册 Controllers 或 HttpClient。
            // 那些应该由主程序的 Program.cs 自己去注册。

            return services; 
        }

        /// <summary>
        /// 启动 OTLP 恢复探测定时器，检测到恢复后输出提示。
        /// </summary>
        private static void StartOtlpRecoveryMonitor(IConfiguration configuration, string credentials, IDictionary<string, object> resourceAttributes, string otlpLogsEndpoint, params string[] endpoints)
        {
            var autoRecover = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverProbe") ?? true;
            if (!autoRecover)
            {
                return;
            }

            var intervalMs = Math.Clamp(configuration.GetValue<int?>("Logger:Otlp:AutoRecoverProbeIntervalMs") ?? 30000, 1000, 300000);
            var timeoutMs = Math.Clamp(configuration.GetValue<int?>("Logger:Otlp:ProbeTimeoutMs") ?? 200, 50, 5000);

            lock (otlpRecoveryTimerLock)
            {
                Interlocked.Exchange(ref _otlpRecoveryNotified, 0);
                _otlpRecoveryTimer?.Dispose();
                _otlpRecoveryTimer = new Timer(_ =>
                {
                    // 步骤1：周期检查端点连通性。
                    // 为什么：服务可能在应用启动后才恢复可用。
                    // 风险点：频率过高会产生无意义探测流量。
                    var recovered = endpoints.All(endpoint => IsEndpointReachable(endpoint, timeoutMs));
                    if (!recovered)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _otlpRecoveryNotified, 1, 0) != 0)
                    {
                        return;
                    }

                    // 步骤2：可选执行日志导出热恢复。
                    // 为什么：端点恢复后让 Logs/Tracing/Metrics 尽量统一进入自动恢复态。
                    // 风险点：若日志器重建失败，日志仍仅写本地兜底文件。
                    var applyForLogs = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForLogs") ?? true;
                    var applyForTelemetry = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForTelemetry") ?? true;
                    if (applyForLogs)
                    {
                        try
                        {
                            var newLogger = CreateLoggerConfiguration(configuration, resourceAttributes, credentials, otlpLogsEndpoint, enableOtlp: true)
                                .CreateLogger();
                            var oldLogger = Log.Logger;
                            Log.Logger = newLogger;
                            (oldLogger as IDisposable)?.Dispose();
                            var telemetryTip = applyForTelemetry
                                ? "Tracing/Metrics 导出器已保持启用，端点恢复后会自动继续上报。"
                                : "Tracing/Metrics 未启用自动恢复，需重启应用后生效。";
                            WriteOtlpFallbackNotice($"检测到 OTLP 端点已恢复可用，日志导出已自动恢复。{telemetryTip}");
                        }
                        catch (Exception ex)
                        {
                            WriteOtlpFallbackNotice($"OTLP 日志导出热恢复失败：{ex.Message}");
                        }
                    }
                    else
                    {
                        var logTip = "日志导出未配置自动热恢复，需重启应用后生效。";
                        var telemetryTip = applyForTelemetry
                            ? "Tracing/Metrics 导出器已保持启用，端点恢复后会自动继续上报。"
                            : "Tracing/Metrics 未启用自动恢复，需重启应用后生效。";
                        WriteOtlpFallbackNotice($"检测到 OTLP 端点已恢复可用。{logTip}{telemetryTip}");
                    }

                    lock (otlpRecoveryTimerLock)
                    {
                        _otlpRecoveryTimer?.Dispose();
                        _otlpRecoveryTimer = null;
                    }
                }, null, intervalMs, intervalMs);
            }
        }

        /// <summary>
        /// 创建 Serilog 配置，按开关决定是否挂载 OTLP 日志导出器。
        /// </summary>
        private static LoggerConfiguration CreateLoggerConfiguration(
            IConfiguration configuration,
            IDictionary<string, object> resourceAttributes,
            string credentials,
            string otlpLogsEndpoint,
            bool enableOtlp)
        {
            var loggerConfig = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .MinimumLevel.ControlledBy(LoggerLevelManager.LogSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .Enrich.WithProperty("MachineName", GlobalDeviceInfo.MachineName)
                .Enrich.WithProperty("AppVersion", GlobalDeviceInfo.AppVersion)
                .Enrich.WithProperty("IPAddress", GlobalDeviceInfo.IpAddress)
                .Enrich.WithProperty("MACAddress", GlobalDeviceInfo.MacAddress)
                .WriteTo.Console()
                .WriteTo.File(
                    path: Path.Combine("logs", "fallback.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true);

            if (enableOtlp)
            {
                loggerConfig = loggerConfig.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpLogsEndpoint;
                    options.Protocol = OtlpProtocol.HttpProtobuf;
                    options.Headers = new Dictionary<string, string> { ["Authorization"] = $"Basic {credentials}" };
                    options.ResourceAttributes = resourceAttributes;
                    options.BatchingOptions.BatchSizeLimit = 10;
                    options.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(500);
                });
            }

            return loggerConfig;
        }

        /// <summary>
        /// 根据当前 OTLP 配置构建 SelfLog 过滤关键字。
        /// </summary>
        private static void UpdateOtlpSelfLogKeywords(params string[] endpoints)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "opentelemetry",
                "otlp"
            };

            foreach (var endpoint in endpoints)
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(uri.Host))
                {
                    keywords.Add(uri.Host.ToLowerInvariant());
                }

                if (!uri.IsDefaultPort)
                {
                    keywords.Add(uri.Port.ToString());
                }

                var lastPathSegment = uri.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault();

                if (!string.IsNullOrWhiteSpace(lastPathSegment))
                {
                    keywords.Add(lastPathSegment.ToLowerInvariant());
                }
            }

            _otlpSelfLogKeywords = keywords.ToArray();
        }

        /// <summary>
        /// 根据配置与启动探测结果决定是否启用 OTLP 远端导出。
        /// </summary>
        private static bool ShouldEnableOtlp(IConfiguration configuration, out string reason, params string[] endpoints)
        {
            // 步骤1：读取显式开关。
            // 为什么：允许生产环境快速一键禁用远端导出。
            // 风险点：若误关开关，将只保留本地日志与本地指标处理。
            var enabled = configuration.GetValue<bool?>("Logger:Otlp:Enabled") ?? true;
            if (!enabled)
            {
                reason = "Logger:Otlp:Enabled=false";
                return false;
            }

            // 步骤2：按配置决定是否做启动探测。
            // 为什么：本地调试场景常见服务未启动，提前降级可减少后续失败重试。
            // 风险点：若关闭探测，服务不可达时仍会走导出重试路径。
            var probeOnStartup = configuration.GetValue<bool?>("Logger:Otlp:ProbeOnStartup") ?? true;
            if (!probeOnStartup)
            {
                reason = string.Empty;
                return true;
            }

            var timeoutMs = Math.Clamp(configuration.GetValue<int?>("Logger:Otlp:ProbeTimeoutMs") ?? 200, 50, 5000);
            var unreachable = endpoints.FirstOrDefault(endpoint => !IsEndpointReachable(endpoint, timeoutMs));
            if (!string.IsNullOrWhiteSpace(unreachable))
            {
                reason = $"Endpoint unreachable: {unreachable}";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// 记录 OTLP 降级提示，避免误以为远端日志已成功上报。
        /// </summary>
        private static void WriteOtlpFallbackNotice(string message)
        {
            var alert = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Logger] {message}{Environment.NewLine}";
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
        }

        /// <summary>
        /// 仅对本地地址执行轻量连通性探测，远端地址默认放行。
        /// </summary>
        private static bool IsEndpointReachable(string endpoint, int timeoutMs)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 步骤1：仅探测本机地址。
            // 为什么：避免启动阶段因外网 DNS/网络抖动造成额外等待。
            // 风险点：远端地址不可达将不会被启动探测拦截。
            if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var port = uri.IsDefaultPort
                ? (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : uri.Port;

            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(uri.Host, port);
                return connectTask.Wait(timeoutMs) && tcpClient.Connected;
            }
            catch
            {
                return false;
            }
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
                var hit = _otlpSelfLogKeywords.Any(k => lower.Contains(k));
                if (!hit)
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
