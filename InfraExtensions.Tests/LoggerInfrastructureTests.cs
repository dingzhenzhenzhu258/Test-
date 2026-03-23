using System.Net;
using System.Net.Sockets;
using System.Reflection;
using LoggerExtensionsHost = Logger.Extensions.LoggerExtensions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfraExtensions.Tests;

public sealed class LoggerInfrastructureTests : IDisposable
{
    private readonly string _logsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    public LoggerInfrastructureTests()
    {
        CleanupLogs();
        Log.Logger = new LoggerConfiguration().CreateLogger();
    }

    [Fact]
    public void ReplayQueued_WithBatchLimit_ShouldKeepUnreplayedEntries()
    {
        var queueManagerType = GetQueueManagerType();
        var sink = new CollectingSink();

        InvokeStatic(queueManagerType, "Configure", 1L * 1024 * 1024, 10L * 1024 * 1024, 72, "DropOldest");
        InvokeStatic(queueManagerType, "Enqueue", "Information", "first", null);
        InvokeStatic(queueManagerType, "Enqueue", "Information", "second", null);
        InvokeStatic(queueManagerType, "Enqueue", "Information", "third", null);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        InvokeStatic(queueManagerType, "ReplayQueued", 2, new Action<string>(_ => { }));

        Assert.Equal(2, sink.Events.Count);
        var afterFirstReplay = LoggerExtensionsHost.GetReplayQueueMetrics();
        Assert.Equal(1, afterFirstReplay.totalEntries);

        InvokeStatic(queueManagerType, "ReplayQueued", 10, new Action<string>(_ => { }));

        Assert.Equal(3, sink.Events.Count);
        var afterSecondReplay = LoggerExtensionsHost.GetReplayQueueMetrics();
        Assert.Equal(0, afterSecondReplay.totalEntries);
    }

    [Fact]
    public void GetReplayQueueDiagnostics_AfterFailedVerifiedReplay_ShouldExposePendingConfirmation()
    {
        var queueManagerType = GetQueueManagerType();

        InvokeStatic(queueManagerType, "Configure", 1L * 1024 * 1024, 10L * 1024 * 1024, 72, "DropOldest");
        InvokeStatic(
            queueManagerType,
            "ConfigureOtlpReplay",
            "http://127.0.0.1:1/api/default/v1/logs",
            "Authorization=Basic Zm9vOmJhcg==",
            new Dictionary<string, object>(),
            new Func<int>(() => 0));
        InvokeStatic(queueManagerType, "Enqueue", "Information", "pending-confirmation", null);

        InvokeStatic(queueManagerType, "ReplayQueued", 10, new Action<string>(_ => { }));

        var diagnostics = LoggerExtensionsHost.GetReplayQueueDiagnostics();
        Assert.Equal(1, diagnostics.TotalEntries);
        Assert.Equal(1, diagnostics.PendingConfirmationEntries);
        Assert.True(diagnostics.ReplayFailureCount >= 1);
        Assert.NotNull(diagnostics.LastReplayAttemptUtc);
    }

    [Fact]
    public void GetReplayQueueDiagnostics_AfterSuccessfulReplay_ShouldExposeLastSuccessTime()
    {
        var queueManagerType = GetQueueManagerType();
        var sink = new CollectingSink();

        InvokeStatic(queueManagerType, "Configure", 1L * 1024 * 1024, 10L * 1024 * 1024, 72, "DropOldest");
        InvokeStatic(queueManagerType, "Enqueue", "Information", "successful-replay", null);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        InvokeStatic(queueManagerType, "ReplayQueued", 10, new Action<string>(_ => { }));

        var diagnostics = LoggerExtensionsHost.GetReplayQueueDiagnostics();
        Assert.Equal(0, diagnostics.TotalEntries);
        Assert.True(diagnostics.SuccessfulReplayCount >= 1);
        Assert.NotNull(diagnostics.LastSuccessfulReplayUtc);
    }

    [Fact]
    public void BuildOtlpHeaders_WithExplicitHeaders_ShouldUseConfiguredValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logger:Otlp:Headers"] = "Authorization=Bearer test-token"
            })
            .Build();

        var headers = InvokePrivateStatic<string?>(typeof(LoggerExtensionsHost), "BuildOtlpHeaders", configuration);

        Assert.Equal("Authorization=Bearer test-token", headers);
    }

    [Fact]
    public void BuildOtlpHeaders_WithUsernamePassword_ShouldCreateBasicAuthHeader()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logger:Otlp:Username"] = "alice",
                ["Logger:Otlp:Password"] = "secret"
            })
            .Build();

        var headers = InvokePrivateStatic<string?>(typeof(LoggerExtensionsHost), "BuildOtlpHeaders", configuration);

        Assert.Equal("Authorization=Basic YWxpY2U6c2VjcmV0", headers);
    }

    [Fact]
    public void CreateLoggerConfiguration_WhenOffline_ShouldQueueDirectLogWrites()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:AppLogPath"] = "logs\\app.log",
                ["Serilog:ReplayLogPath"] = "logs\\replay.log",
                ["Logger:Otlp:ReplayQueueEnabled"] = "true"
            })
            .Build();

        InvokePrivateStatic(typeof(LoggerExtensionsHost), "SetReplayQueueAvailability", true);
        InvokePrivateStatic(typeof(LoggerExtensionsHost), "SetOtlpAvailability", false);

        var loggerConfiguration = InvokePrivateStatic<LoggerConfiguration>(
            typeof(LoggerExtensionsHost),
            "CreateLoggerConfiguration",
            configuration,
            new Dictionary<string, object>(),
            null,
            "http://localhost:5080/api/default/v1/logs",
            false);

        Log.Logger = loggerConfiguration.CreateLogger();
        Log.Information("direct logger message {Value}", 42);

        var metrics = LoggerExtensionsHost.GetReplayQueueMetrics();
        Assert.Equal(1, metrics.totalEntries);
    }

    [Fact]
    public void WriteOtlpFallbackNotice_WhenOffline_ShouldEnqueueLifecycleLog()
    {
        var queueManagerType = GetQueueManagerType();
        InvokeStatic(queueManagerType, "Configure", 1L * 1024 * 1024, 10L * 1024 * 1024, 72, "DropOldest");
        InvokePrivateStatic(typeof(LoggerExtensionsHost), "SetReplayQueueAvailability", true);
        InvokePrivateStatic(typeof(LoggerExtensionsHost), "SetOtlpAvailability", false);

        InvokePrivateStatic<object?>(typeof(LoggerExtensionsHost), "WriteOtlpFallbackNotice", "OTLP disconnected");

        var metrics = LoggerExtensionsHost.GetReplayQueueMetrics();
        Assert.Equal(1, metrics.totalEntries);
    }

    [Fact]
    public void IsEndpointReachable_WithClosedNonLocalLoopbackPort_ShouldReturnFalse()
    {
        var unusedPort = GetUnusedTcpPort();
        var reachable = InvokePrivateStatic<bool>(typeof(LoggerExtensionsHost), "IsEndpointReachable", $"http://127.0.0.2:{unusedPort}/v1/logs", 100);

        Assert.False(reachable);
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        CleanupLogs();
    }

    private void CleanupLogs()
    {
        if (Directory.Exists(_logsRoot))
        {
            Directory.Delete(_logsRoot, recursive: true);
        }
    }

    private static Type GetQueueManagerType()
    {
        return typeof(LoggerExtensionsHost).Assembly.GetType("Logger.Internals.OtlpReplayQueueManager", throwOnError: true)!;
    }

    private static object? InvokeStatic(Type type, string methodName, params object?[]? args)
    {
        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        var argCount = args?.Length ?? 0;
        var method = candidates.SingleOrDefault(m => m.GetParameters().Length == argCount)
            ?? candidates.SingleOrDefault();
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[]? args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }

    private static void InvokePrivateStatic(Type type, string methodName, params object?[]? args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, args);
    }

    private static int GetUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }
}
