using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Sinks.OpenTelemetry;
using Serilog.Settings.Configuration;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Logger.Internals;

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
        private static int _processExitHooked;
        private static int _otlpAlertRaised;
        private static int _otlpFailureVersion;
        private static string[] _otlpSelfLogKeywords = new[] { "opentelemetry", "otlp", "5080" };
        private static int _otlpRecoveryNotified;
        private static Timer? _otlpRecoveryTimer;
        private static TracerProvider? _runtimeTracerProvider;
        private static MeterProvider? _runtimeMeterProvider;
        private static readonly object otlpRecoveryTimerLock = new();
        private static readonly object otlpTelemetryRuntimeLock = new();
        private static int _otlpAvailableFlag = 1;
        private static int _replayQueueEnabledFlag = 1;
        private static Action? _restartRecoveryMonitor;
        private static Action? _drainReplayQueueOnProcessExit;
        private static OtlpForwardingSink? _otlpForwardingSink;
        private static readonly object recentLogsLock = new();
        private static readonly Queue<RecentLogEntry> recentLogs = new();
        private const int RecentLogBufferLimit = 512;
        private static readonly Regex FailedBatchRegex = new(@"failed emitting a batch \((?:[^,]+),\s*(\d+)\s+events\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string DefaultConsoleOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}.{MemberName} (Line: {LineNumber})  Message: {Message:lj}{NewLine}{Exception}{NewLine}";
        private const string DefaultFileOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] Method: {SourceContext}.{MemberName} (Line: {LineNumber})  Message: {Message:lj}{NewLine}{Exception}{NewLine}";

        public static readonly ActivitySource TraceSource = new("Hardware.Tracer");

        public static bool IsOtlpAvailable => Volatile.Read(ref _otlpAvailableFlag) == 1;

        public static void EnsureConfigInitialized()
        {
            EnsureConfigInitialized(null, "appsettings.json", msg => Console.WriteLine(msg));
        }

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

        public static Activity? StartTrace(
            string operationName,
            [CallerMemberName] string memberName = "",
            [CallerFilePath] string sourceFilePath = "")
        {
            var activity = TraceSource.StartActivity(operationName);
            activity?.SetTag("code.function", memberName);
            activity?.SetTag("code.filepath", Path.GetFileName(sourceFilePath));
            return activity;
        }

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

            var otlpHeaders = BuildOtlpHeaders(configuration);
            var otlpLogsEndpoint = configuration["Logger:Otlp:LogsEndpoint"] ?? "http://localhost:5080/api/default/v1/logs";
            var otlpTracesEndpoint = configuration["Logger:Otlp:TracesEndpoint"] ?? "http://localhost:5080/api/default/v1/traces";
            var otlpMetricsEndpoint = configuration["Logger:Otlp:MetricsEndpoint"] ?? "http://localhost:5080/api/default/v1/metrics";

            UpdateOtlpSelfLogKeywords(otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);

            var otlpConfigured = configuration.GetValue<bool?>("Logger:Otlp:Enabled") ?? true;
            var otlpEnabled = ShouldEnableOtlp(configuration, out var otlpDisabledReason, otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);
            var autoRecoverTelemetry = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForTelemetry") ?? true;
            var telemetryRuntimeSwitch = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForTelemetryRuntimeSwitch") ?? false;
            var enableTelemetryExporters = otlpEnabled || (autoRecoverTelemetry && !telemetryRuntimeSwitch);
            var replayQueueEnabled = configuration.GetValue<bool?>("Logger:Otlp:ReplayQueueEnabled") ?? true;
            SetReplayQueueAvailability(replayQueueEnabled);

            // 步骤1.4：下发补传队列容量配置。
            // 为什么：允许按环境调节磁盘占用与保留时长，避免硬编码。
            // 风险点：配置过小会增加队列淘汰概率，影响离线补传覆盖率。
            var replayMaxFileSizeMb = Math.Max(1, configuration.GetValue<int?>("Logger:Otlp:ReplayQueueMaxFileSizeMb") ?? 50);
            var replayMaxTotalSizeMb = Math.Max(replayMaxFileSizeMb, configuration.GetValue<int?>("Logger:Otlp:ReplayQueueMaxTotalSizeMb") ?? 500);
            var replayMaxAgeHours = Math.Max(1, configuration.GetValue<int?>("Logger:Otlp:ReplayQueueMaxAgeHours") ?? 72);
            var replayOverflowStrategy = configuration["Logger:Otlp:ReplayQueueOverflowStrategy"] ?? "DropOldest";
            OtlpReplayQueueManager.Configure(
                replayMaxFileSizeMb * 1024L * 1024L,
                replayMaxTotalSizeMb * 1024L * 1024L,
                replayMaxAgeHours,
                replayOverflowStrategy);

            SetOtlpAvailability(otlpEnabled);

            var resourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = projectName,
                ["environment"] = "development",
                ["service.version"] = "1.0.0"
            };

            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(projectName)
                .AddAttributes(resourceAttributes);
            OtlpReplayQueueManager.ConfigureOtlpReplay(
                otlpLogsEndpoint,
                otlpHeaders,
                resourceAttributes,
                () => Volatile.Read(ref _otlpFailureVersion));

            var loggerConfig = CreateLoggerConfiguration(configuration, resourceAttributes, otlpHeaders, otlpLogsEndpoint, otlpConfigured);

            // 步骤1.5：保存恢复探测重启入口，供运行时 SelfLog 检测到二次断开时调用。
            // 为什么：恢复后 Timer 已自毁，若再次断开需要能重新启动探测循环。
            // 风险点：闭包捕获了 configuration 等引用，生命周期与进程一致。
            _restartRecoveryMonitor = () => StartOtlpRecoveryMonitor(
                configuration, otlpHeaders, resourceAttributes, projectName, isWebApi,
                otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);
            _drainReplayQueueOnProcessExit = () =>
            {
                if (!IsOtlpAvailable)
                {
                    return;
                }

                for (var attempt = 0; attempt < 5; attempt++)
                {
                    ReplayQueuedLogsIfAny(configuration);
                    var (_, _, queueEntries, _) = GetReplayQueueMetrics();
                    if (queueEntries <= 0 || !IsOtlpAvailable)
                    {
                        break;
                    }

                    Thread.Sleep(1000);
                }
            };

            if (!otlpEnabled)
            {
                WriteOtlpFallbackNotice($"启动时已禁用 OTLP 导出，仅保留本地日志。原因: {otlpDisabledReason}");

                if (autoRecoverTelemetry)
                {
                    var telemetryNotice = telemetryRuntimeSwitch
                        ? "Tracing/Metrics 已启用运行时热切换模式，端点恢复后将自动重建导出器。"
                        : "Tracing/Metrics 导出器已保持启用状态，待 OTLP 端点恢复后将自动恢复上报。";
                    WriteOtlpFallbackNotice(telemetryNotice);
                }

                StartOtlpRecoveryMonitor(configuration, otlpHeaders, resourceAttributes, projectName, isWebApi, otlpLogsEndpoint, otlpTracesEndpoint, otlpMetricsEndpoint);
            }

            Log.Logger = loggerConfig.CreateLogger();
            HookProcessExitFlushOnce();

            if (otlpEnabled)
            {
                ReplayQueuedLogsIfAny(configuration);
            }

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog(dispose: true);
            });

            Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

            services.AddOpenTelemetry()
                .WithTracing(tracingBuilder =>
                {
                    tracingBuilder.SetResourceBuilder(resourceBuilder)
                        .AddSource(projectName)
                        .AddSource("Hardware.Tracer")
                        .AddHttpClientInstrumentation();

                    if (isWebApi)
                    {
                        tracingBuilder.AddAspNetCoreInstrumentation();
                    }

                    if (enableTelemetryExporters)
                    {
                        tracingBuilder.AddOtlpExporter(opts =>
                        {
                            opts.Endpoint = new Uri(otlpTracesEndpoint);
                            opts.Headers = otlpHeaders;
                            opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                        });
                    }
                })
                .WithMetrics(metricsBuilder =>
                {
                    metricsBuilder.SetResourceBuilder(resourceBuilder)
                        .AddRuntimeInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddMeter("SerialPortService.GenericHandler")
                        .AddMeter(projectName);

                    if (isWebApi)
                    {
                        metricsBuilder.AddAspNetCoreInstrumentation();
                    }

                    if (enableTelemetryExporters)
                    {
                        metricsBuilder.AddOtlpExporter(opts =>
                        {
                            opts.Endpoint = new Uri(otlpMetricsEndpoint);
                            opts.Headers = otlpHeaders;
                            opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                        });
                    }
                });

            return services;
        }

        public static void EnqueueReplayLogIfNeeded(LogLevel level, string message, Exception? exception = null)
        {
            if (IsOtlpAvailable)
            {
                return;
            }

            OtlpReplayQueueManager.Enqueue(level.ToString(), message, exception?.ToString());
        }

        public static (int fileCount, long totalBytes, long totalEntries, long replayFailures) GetReplayQueueMetrics()
        {
            var metrics = OtlpReplayQueueManager.GetMetrics();
            return (metrics.FileCount, metrics.TotalBytes, metrics.TotalEntries, metrics.ReplayFailureCount);
        }

        public static ReplayQueueDiagnostics GetReplayQueueDiagnostics()
        {
            var metrics = OtlpReplayQueueManager.GetMetrics();
            return new ReplayQueueDiagnostics
            {
                FileCount = metrics.FileCount,
                TempFileCount = metrics.TempFileCount,
                TotalBytes = metrics.TotalBytes,
                TotalEntries = metrics.TotalEntries,
                PendingConfirmationEntries = metrics.PendingConfirmationEntries,
                ReplayFailureCount = metrics.ReplayFailureCount,
                SuccessfulReplayCount = metrics.SuccessfulReplayCount,
                LastSuccessfulReplayUtc = metrics.LastSuccessfulReplayUtc,
                LastReplayAttemptUtc = metrics.LastReplayAttemptUtc
            };
        }

        private static void StartOtlpRecoveryMonitor(
            IConfiguration configuration,
            string? otlpHeaders,
            IDictionary<string, object> resourceAttributes,
            string projectName,
            bool isWebApi,
            string otlpLogsEndpoint,
            string otlpTracesEndpoint,
            string otlpMetricsEndpoint)
        {
            var autoRecover = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverProbe") ?? true;
            if (!autoRecover)
            {
                return;
            }

            var intervalMs = Math.Clamp(configuration.GetValue<int?>("Logger:Otlp:AutoRecoverProbeIntervalMs") ?? 30000, 1000, 300000);
            var timeoutMs = Math.Clamp(configuration.GetValue<int?>("Logger:Otlp:ProbeTimeoutMs") ?? 200, 50, 5000);
            var dueTimeMs = Math.Min(1000, intervalMs);

            lock (otlpRecoveryTimerLock)
            {
                Interlocked.Exchange(ref _otlpRecoveryNotified, 0);
                _otlpRecoveryTimer?.Dispose();
                _otlpRecoveryTimer = new Timer(_ =>
                {
                    try
                    {
                        var logsRecovered = IsEndpointReachable(otlpLogsEndpoint, timeoutMs);
                        var tracesRecovered = IsEndpointReachable(otlpTracesEndpoint, timeoutMs);
                        var metricsRecovered = IsEndpointReachable(otlpMetricsEndpoint, timeoutMs);
                        if (!logsRecovered && !tracesRecovered && !metricsRecovered)
                        {
                            return;
                        }

                        if (Interlocked.CompareExchange(ref _otlpRecoveryNotified, 1, 0) != 0)
                        {
                            return;
                        }

                        var applyForLogs = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForLogs") ?? true;
                        var applyForTelemetry = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForTelemetry") ?? true;
                        var telemetryRuntimeSwitch = configuration.GetValue<bool?>("Logger:Otlp:AutoRecoverApplyForTelemetryRuntimeSwitch") ?? false;
                        var shouldRecoverLogs = applyForLogs && logsRecovered;
                        var shouldRecoverTelemetry = applyForTelemetry && tracesRecovered && metricsRecovered;
                        var logsRecoverySatisfied = !applyForLogs || shouldRecoverLogs;
                        var telemetryRecoverySatisfied = !applyForTelemetry || !telemetryRuntimeSwitch || shouldRecoverTelemetry;

                        if (!logsRecoverySatisfied || !telemetryRecoverySatisfied)
                        {
                            Interlocked.Exchange(ref _otlpRecoveryNotified, 0);
                            return;
                        }

                        string telemetryTip;
                        if (applyForTelemetry && telemetryRuntimeSwitch && shouldRecoverTelemetry)
                        {
                            var telemetryRebuilt = TryRebuildTelemetryRuntimeProviders(
                                isWebApi,
                                projectName,
                                resourceAttributes,
                                otlpHeaders,
                                otlpTracesEndpoint,
                                otlpMetricsEndpoint,
                                out var telemetryError);

                            telemetryTip = telemetryRebuilt
                                ? "Tracing/Metrics 导出器已完成运行时热重建并恢复上报。"
                                : $"Tracing/Metrics 运行时热重建失败：{telemetryError}";
                        }
                        else if (applyForTelemetry)
                        {
                            telemetryTip = "Tracing/Metrics 导出器已保持启用，端点恢复后会自动继续上报。";
                        }
                        else
                        {
                            telemetryTip = "Tracing/Metrics 未启用自动恢复，需重启应用后生效。";
                        }

                        var logsRecoveredNow = false;
                        if (applyForLogs)
                        {
                            try
                            {
                                if (!IsOtlpAvailable)
                                {
                                    _otlpForwardingSink?.Recreate();
                                    SetOtlpAvailability(true);
                                    logsRecoveredNow = true;
                                }

                                long queueEntries = 0;
                                for (var replayAttempt = 0; replayAttempt < 5; replayAttempt++)
                                {
                                    ReplayQueuedLogsIfAny(configuration);
                                    Interlocked.Exchange(ref _otlpAlertRaised, 0);

                                    (_, _, queueEntries, _) = GetReplayQueueMetrics();
                                    if (queueEntries <= 0 || !IsOtlpAvailable)
                                    {
                                        break;
                                    }

                                    Thread.Sleep(1500);
                                }

                                if (queueEntries > 0)
                                {
                                    WriteOtlpFallbackNotice($"OTLP 端点已恢复，但仍有 {queueEntries} 条离线日志留在补传队列中，将在下一次探测周期继续重试。");
                                    Interlocked.Exchange(ref _otlpRecoveryNotified, 0);
                                    return;
                                }

                                WriteOtlpFallbackNotice($"检测到 OTLP 端点已恢复可用，日志导出已自动恢复。{telemetryTip}");
                            }
                            catch (Exception ex)
                            {
                                WriteOtlpFallbackNotice($"OTLP 日志导出热恢复失败：{ex.Message}");
                                if (logsRecoveredNow)
                                {
                                    SetOtlpAvailability(false);
                                }
                                Interlocked.Exchange(ref _otlpRecoveryNotified, 0);
                                return;
                            }
                        }
                        else
                        {
                            var logTip = "日志导出未配置自动热恢复，需重启应用后生效。";
                            WriteOtlpFallbackNotice($"检测到 OTLP 端点已恢复可用。{logTip}{telemetryTip}");
                        }

                        // 步骤4：重置故障标记，允许 SelfLog 再次检测未来的 OTLP 断开。
                        // 为什么：_otlpAlertRaised=1 时 SelfLog 回调会直接跳过，无法感知二次故障。
                        // 风险点：重置与 Timer 销毁之间的窗口期内 SelfLog 可能触发，
                        //        但 StartOtlpRecoveryMonitor 内部有 lock 保护，不会竞态。
                        lock (otlpRecoveryTimerLock)
                        {
                            _otlpRecoveryTimer?.Dispose();
                            _otlpRecoveryTimer = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 步骤5：顶层异常兜底，防止 Timer 回调崩溃导致恢复永久卡死。
                        // 为什么：CAS 将 _otlpRecoveryNotified 置 1 后若抛异常，
                        //        后续 tick 全部在 CAS 处 return，恢复逻辑永远无法执行。
                        // 风险点：重置为 0 后下次 tick 会重试，不会无限循环（有 recovered 前置检查）。
                        Interlocked.Exchange(ref _otlpRecoveryNotified, 0);
                        WriteOtlpFallbackNotice($"恢复探测回调异常，将在下次探测周期重试: {ex.Message}");
                    }
                }, null, dueTimeMs, intervalMs);
            }
        }

        private static bool TryRebuildTelemetryRuntimeProviders(
            bool isWebApi,
            string projectName,
            IDictionary<string, object> resourceAttributes,
            string? otlpHeaders,
            string otlpTracesEndpoint,
            string otlpMetricsEndpoint,
            out string errorMessage)
        {
            lock (otlpTelemetryRuntimeLock)
            {
                TracerProvider? newTracerProvider = null;
                MeterProvider? newMeterProvider = null;
                try
                {
                    var tracingResourceBuilder = ResourceBuilder.CreateDefault()
                        .AddService(projectName)
                        .AddAttributes(resourceAttributes);

                    var tracerProviderBuilder = Sdk.CreateTracerProviderBuilder()
                        .SetResourceBuilder(tracingResourceBuilder)
                        .AddSource(projectName)
                        .AddSource("Hardware.Tracer")
                        .AddHttpClientInstrumentation();

                    if (isWebApi)
                    {
                        tracerProviderBuilder.AddAspNetCoreInstrumentation();
                    }

                    tracerProviderBuilder.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpTracesEndpoint);
                        opts.Headers = otlpHeaders;
                        opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    });

                    newTracerProvider = tracerProviderBuilder.Build();

                    var metricsResourceBuilder = ResourceBuilder.CreateDefault()
                        .AddService(projectName)
                        .AddAttributes(resourceAttributes);

                    var meterProviderBuilder = Sdk.CreateMeterProviderBuilder()
                        .SetResourceBuilder(metricsResourceBuilder)
                        .AddRuntimeInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddMeter("SerialPortService.GenericHandler")
                        .AddMeter(projectName);

                    if (isWebApi)
                    {
                        meterProviderBuilder.AddAspNetCoreInstrumentation();
                    }

                    meterProviderBuilder.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(otlpMetricsEndpoint);
                        opts.Headers = otlpHeaders;
                        opts.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    });

                    newMeterProvider = meterProviderBuilder.Build();

                    _runtimeTracerProvider?.Dispose();
                    _runtimeMeterProvider?.Dispose();
                    _runtimeTracerProvider = newTracerProvider;
                    _runtimeMeterProvider = newMeterProvider;
                    errorMessage = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    newTracerProvider?.Dispose();
                    newMeterProvider?.Dispose();
                    errorMessage = ex.Message;
                    return false;
                }
            }
        }

        private static LoggerConfiguration CreateLoggerConfiguration(
            IConfiguration configuration,
            IDictionary<string, object> resourceAttributes,
            string? otlpHeaders,
            string otlpLogsEndpoint,
            bool otlpConfigured)
        {
            var consoleOutputTemplate = configuration["Serilog:ConsoleOutputTemplate"] ?? DefaultConsoleOutputTemplate;
            var fileOutputTemplate = configuration["Serilog:FileOutputTemplate"] ?? DefaultFileOutputTemplate;
            var appLogPath = ResolveLogPath(configuration["Serilog:AppLogPath"] ?? Path.Combine("logs", "app.log"));
            var replayLogPath = ResolveLogPath(configuration["Serilog:ReplayLogPath"] ?? Path.Combine("logs", "replay.log"));
            var retainedFileCountLimit = Math.Max(1, configuration.GetValue<int?>("Serilog:RetainedFileCountLimit") ?? 7);

            var loggerConfig = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration, new ConfigurationReaderOptions())
                .MinimumLevel.ControlledBy(LoggerLevelManager.LogSwitch)
                .Enrich.FromLogContext()
                .Enrich.With(new EventIdentityEnricher())
                .Enrich.WithExceptionDetails()
                .Enrich.WithProperty("MachineName", GlobalDeviceInfo.MachineName)
                .Enrich.WithProperty("AppVersion", GlobalDeviceInfo.AppVersion)
                .Enrich.WithProperty("IPAddress", GlobalDeviceInfo.IpAddress)
                .Enrich.WithProperty("MACAddress", GlobalDeviceInfo.MacAddress)
                .WriteTo.Sink(new RecentLogBufferSink())
                .WriteTo.Sink(new ReplayQueueSink())
                .WriteTo.Console(outputTemplate: consoleOutputTemplate)
                .WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(IsReplayFileEvent)
                    .WriteTo.File(
                        appLogPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: retainedFileCountLimit,
                        outputTemplate: fileOutputTemplate))
                .WriteTo.Logger(lc => lc
                    .Filter.ByIncludingOnly(IsReplayFileEvent)
                    .WriteTo.File(
                        replayLogPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: retainedFileCountLimit,
                        outputTemplate: fileOutputTemplate));

            // 步骤1：仅保留配置文件中的常规 sink，不再把 fallback.log 作为常驻文件 sink。
            // 为什么：避免 app.log 与 fallback.log 同时写入同一批业务日志，造成重复落盘。
            // 风险点：fallback.log 现在只用于 OTLP 失败提示和补传异常，不再承载常规业务日志。

            if (otlpConfigured)
            {
                _otlpForwardingSink = new OtlpForwardingSink(resourceAttributes, otlpHeaders, otlpLogsEndpoint);
                loggerConfig = loggerConfig.WriteTo.Sink(_otlpForwardingSink);
            }

            return loggerConfig;
        }

        private static string ResolveLogPath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        private static bool IsReplayEvent(Serilog.Events.LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue("ReplayQueue", out var replayValue))
            {
                return false;
            }

            return replayValue is Serilog.Events.ScalarValue scalar
                && scalar.Value is bool flag
                && flag;
        }

        private static bool IsReplayLocalOnlyEvent(Serilog.Events.LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue("ReplayLocalOnly", out var replayValue))
            {
                return false;
            }

            return replayValue is Serilog.Events.ScalarValue scalar
                && scalar.Value is bool flag
                && flag;
        }

        private static bool IsReplayFileEvent(Serilog.Events.LogEvent logEvent)
        {
            return IsReplayEvent(logEvent) || IsReplayLocalOnlyEvent(logEvent);
        }

        private static void BufferRecentLog(LogEvent logEvent)
        {
            if (IsReplayFileEvent(logEvent))
            {
                return;
            }

            var entry = new RecentLogEntry(
                logEvent.Timestamp.UtcDateTime,
                logEvent.Level.ToString(),
                logEvent.RenderMessage(),
                logEvent.Exception?.ToString());

            lock (recentLogsLock)
            {
                recentLogs.Enqueue(entry);
                while (recentLogs.Count > RecentLogBufferLimit)
                {
                    recentLogs.Dequeue();
                }
            }
        }

        private static void RequeueRecentBatchFromSelfLog(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var match = FailedBatchRegex.Match(message);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var batchSize) || batchSize <= 0)
            {
                return;
            }

            RecentLogEntry[] snapshot;
            lock (recentLogsLock)
            {
                snapshot = recentLogs.ToArray();
            }

            var cutoffUtc = DateTime.UtcNow.AddSeconds(-5);
            var candidates = snapshot
                .Where(entry => entry.TimestampUtc >= cutoffUtc)
                .TakeLast(Math.Max(batchSize, 16))
                .ToArray();
            if (candidates.Length == 0)
            {
                candidates = snapshot.TakeLast(Math.Max(batchSize, 16)).ToArray();
            }

            foreach (var entry in candidates)
            {
                OtlpReplayQueueManager.Enqueue(entry.TimestampUtc, entry.Level, entry.Message, entry.Exception);
            }
        }

        private static string? BuildOtlpHeaders(IConfiguration configuration)
        {
            var rawHeaders = configuration["Logger:Otlp:Headers"];
            if (!string.IsNullOrWhiteSpace(rawHeaders))
            {
                return rawHeaders;
            }

            var username = configuration["Logger:Otlp:Username"];
            var password = configuration["Logger:Otlp:Password"];
            if (!string.IsNullOrWhiteSpace(username) && password != null)
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                return $"Authorization=Basic {credentials}";
            }

            return null;
        }

        private static Dictionary<string, string> ParseOtlpHeaders(string headers)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var segments = headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                var separatorIndex = segment.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex == segment.Length - 1)
                {
                    continue;
                }

                var key = segment[..separatorIndex].Trim();
                var value = segment[(separatorIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = value;
                }
            }

            return result;
        }

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

        private static bool ShouldEnableOtlp(IConfiguration configuration, out string reason, params string[] endpoints)
        {
            var enabled = configuration.GetValue<bool?>("Logger:Otlp:Enabled") ?? true;
            if (!enabled)
            {
                reason = "Logger:Otlp:Enabled=false";
                return false;
            }

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

        private static void ReplayQueuedLogsIfAny(IConfiguration configuration)
        {
            var replayEnabled = configuration.GetValue<bool?>("Logger:Otlp:ReplayQueueEnabled") ?? true;
            if (!replayEnabled)
            {
                return;
            }

            var configuredBatchSize = Math.Clamp(configuration.GetValue<int?>("Logger:Otlp:ReplayBatchSize") ?? 200, 1, 5000);
            var batchSize = Math.Min(configuredBatchSize, 20);
            OtlpReplayQueueManager.ReplayQueued(batchSize, WriteOtlpFallbackNotice);
        }

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

            EmitLifecycleLog(message);
        }

        private static void EmitLifecycleLog(string message)
        {
            try
            {
                if (IsOtlpAvailable)
                {
                    Log.ForContext("LoggerInternal", true)
                        .Write(LogEventLevel.Warning, "[LoggerLifecycle] {Message}", message);
                    return;
                }

                if (Volatile.Read(ref _replayQueueEnabledFlag) == 1)
                {
                    OtlpReplayQueueManager.Enqueue("Warning", $"[LoggerLifecycle] {message}", null);
                }
            }
            catch
            {
            }
        }

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

            var port = uri.IsDefaultPort
                ? (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : uri.Port;

            try
            {
                using var tcpClient = new TcpClient();
                var connectTask = tcpClient.ConnectAsync(uri.Host, port);

                // 步骤1：带超时等待 TCP 连接结果。
                // 为什么：阻塞式探测需要有限超时，否则端口不可达时会长时间挂起。
                // 风险点：Wait 超时后 connectTask 仍在后台运行，using 释放 tcpClient
                //        会导致 SocketException(995)，若不观察 Task 则会变成
                //        UnobservedTaskException，被 WPF 全局异常处理器捕获。
                if (connectTask.Wait(timeoutMs))
                {
                    return tcpClient.Connected;
                }

                // 步骤2：超时时主动观察悬挂 Task 的异常，避免 UnobservedTaskException。
                // 为什么：using 释放 Socket 后后台 Task 会以 SocketException 终结，
                //        不观察会在 GC 回收时由 finalizer 线程抛出。
                // 风险点：ContinueWith 回调在线程池执行，开销极低。
                connectTask.ContinueWith(
                    static t => { _ = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);

                return false;
            }
            catch
            {
                return false;
            }
        }

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

                Interlocked.Increment(ref _otlpFailureVersion);
                RequeueRecentBatchFromSelfLog(message);
                SetOtlpAvailability(false);
                _restartRecoveryMonitor?.Invoke();
                WriteOtlpFallbackNotice($"OTLP 导出异常，已切换至离线队列并重启恢复探测。常规业务日志继续写入 app.log。原始消息: {message}");
            });
        }

        private static void HookProcessExitFlushOnce()
        {
            if (Interlocked.CompareExchange(ref _processExitHooked, 1, 0) != 0)
            {
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
            {
                try
                {
                    _drainReplayQueueOnProcessExit?.Invoke();
                }
                catch
                {
                }

                try
                {
                    Log.CloseAndFlush();
                }
                catch
                {
                }
            };
        }

        private static void SetOtlpAvailability(bool available)
        {
            Volatile.Write(ref _otlpAvailableFlag, available ? 1 : 0);
        }

        private static void SetReplayQueueAvailability(bool enabled)
        {
            Volatile.Write(ref _replayQueueEnabledFlag, enabled ? 1 : 0);
        }

        private static string ComputeEventId(DateTime timestampUtc, string level, string message, string? exception)
        {
            var normalizedTimestamp = timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime();
            var seed = string.Join("|",
                normalizedTimestamp.ToString("O"),
                level,
                message,
                exception ?? string.Empty);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private sealed class ReplayQueueSink : Serilog.Core.ILogEventSink
        {
            public void Emit(LogEvent logEvent)
            {
                if (IsOtlpAvailable || Volatile.Read(ref _replayQueueEnabledFlag) != 1 || IsReplayEvent(logEvent) || IsReplayLocalOnlyEvent(logEvent))
                {
                    return;
                }

                try
                {
                    OtlpReplayQueueManager.Enqueue(
                        logEvent.Timestamp.UtcDateTime,
                        logEvent.Level.ToString(),
                        logEvent.RenderMessage(),
                        logEvent.Exception?.ToString());
                }
                catch
                {
                }
            }
        }

        private sealed class RecentLogBufferSink : Serilog.Core.ILogEventSink
        {
            public void Emit(LogEvent logEvent)
            {
                BufferRecentLog(logEvent);
            }
        }

        private sealed class EventIdentityEnricher : ILogEventEnricher
        {
            public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
            {
                if (logEvent.Properties.ContainsKey("EventId"))
                {
                    return;
                }

                var eventId = ComputeEventId(
                    logEvent.Timestamp.UtcDateTime,
                    logEvent.Level.ToString(),
                    logEvent.RenderMessage(),
                    logEvent.Exception?.ToString());
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("EventId", eventId));
            }
        }

        private sealed class OtlpForwardingSink : Serilog.Core.ILogEventSink, IDisposable
        {
            private readonly object _lock = new();
            private readonly IDictionary<string, object> _resourceAttributes;
            private readonly string? _otlpHeaders;
            private readonly string _otlpLogsEndpoint;
            private Serilog.ILogger _otlpLogger;

            public OtlpForwardingSink(
                IDictionary<string, object> resourceAttributes,
                string? otlpHeaders,
                string otlpLogsEndpoint)
            {
                _resourceAttributes = new Dictionary<string, object>(resourceAttributes);
                _otlpHeaders = otlpHeaders;
                _otlpLogsEndpoint = otlpLogsEndpoint;
                _otlpLogger = CreateInnerLogger();
            }

            public void Emit(LogEvent logEvent)
            {
                if (!IsOtlpAvailable || IsReplayLocalOnlyEvent(logEvent))
                {
                    return;
                }

                lock (_lock)
                {
                    _otlpLogger.Write(logEvent);
                }
            }

            public void Recreate()
            {
                lock (_lock)
                {
                    var oldLogger = _otlpLogger;
                    _otlpLogger = CreateInnerLogger();
                    (oldLogger as IDisposable)?.Dispose();
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    (_otlpLogger as IDisposable)?.Dispose();
                }
            }

            private Serilog.ILogger CreateInnerLogger()
            {
                var loggerConfiguration = new LoggerConfiguration()
                    .WriteTo.OpenTelemetry(options =>
                    {
                        options.Endpoint = _otlpLogsEndpoint;
                        options.Protocol = OtlpProtocol.HttpProtobuf;
                        if (!string.IsNullOrWhiteSpace(_otlpHeaders))
                        {
                            options.Headers = ParseOtlpHeaders(_otlpHeaders);
                        }

                        options.ResourceAttributes = _resourceAttributes;
                        options.BatchingOptions.BatchSizeLimit = 1;
                        options.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(100);
                    });

                return loggerConfiguration.CreateLogger();
            }
        }

        public sealed class ReplayQueueDiagnostics
        {
            public int FileCount { get; set; }
            public int TempFileCount { get; set; }
            public long TotalBytes { get; set; }
            public long TotalEntries { get; set; }
            public long PendingConfirmationEntries { get; set; }
            public long ReplayFailureCount { get; set; }
            public long SuccessfulReplayCount { get; set; }
            public DateTime? LastSuccessfulReplayUtc { get; set; }
            public DateTime? LastReplayAttemptUtc { get; set; }
        }

        private sealed record RecentLogEntry(DateTime TimestampUtc, string Level, string Message, string? Exception);
    }
}
