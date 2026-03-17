using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Extensions;
using SerialPortService.Services;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Reflection;
using Xunit;

namespace InfraExtensions.Tests;

public class SerialPortServiceProductionReadinessTests
{
    private static int _factoryRegistered;

    public SerialPortServiceProductionReadinessTests()
    {
        EnsureFactoryRegistered();
    }

    [Fact]
    public async Task TryWrite_WhenPortNotOpen_ShouldReturnFailure()
    {
        var sut = new SerialPortServiceBase();

        var result = await sut.TryWrite("COM_NOT_OPEN", new byte[] { 0x01 });

        Assert.False(result.IsSuccess);
        Assert.Contains("未打开", result.Message);
    }

    [Fact]
    public async Task PortContextSend_WhenNotOpen_ShouldThrowInvalidOperationException()
    {
        using var context = new TestPortContext("COM_UNIT_TEST_SEND");

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Send(new byte[] { 0x01 }));
    }

    [Fact]
    public async Task TryWrite_WhenSendThrows_ShouldReturnFailure()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_THROW_{Guid.NewGuid():N}";

        try
        {
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Controller, ProtocolEnum.Default);
            Assert.True(open.IsSuccess);

            var result = await sut.TryWrite(portName, new byte[] { 0x02 });

            Assert.False(result.IsSuccess);
            Assert.Contains("发送失败", result.Message);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public async Task TryWrite_WhenConcurrentAndCloseAllRepeatedly_ShouldRemainStable()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_OK_{Guid.NewGuid():N}";

        try
        {
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Controller, ProtocolEnum.Default);
            Assert.True(open.IsSuccess);

            var writes = Enumerable.Range(0, 50)
                .Select(_ => sut.TryWrite(portName, new byte[] { 0x10, 0x20 }))
                .ToArray();

            var results = await Task.WhenAll(writes);
            Assert.All(results, r => Assert.True(r.IsSuccess));

            var close1 = sut.CloseAll();
            var close2 = sut.CloseAll();

            Assert.True(close1.IsSuccess);
            Assert.True(close2.IsSuccess);
            Assert.Empty(SerialPortServiceBase.OnlyReadports);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void GenericHandler_AlertThresholds_ShouldEmitExpectedLogs()
    {
        var logger = new TestLogger();
        using var handler = new GenericHandler<string>(
            "COM_ALERT_TEST",
            9600,
            Parity.None,
            8,
            StopBits.One,
            new DummyStringParser(),
            logger,
            new GenericHandlerOptions
            {
                TimeoutRateAlertThresholdPercent = 50,
                TimeoutRateAlertMinSamples = 10,
                WaitBacklogAlertThreshold = 3,
                ResponseChannelCapacity = 8,
                WaitModeQueueCapacity = 8,
                SampleLogInterval = 1
            });

        SetPrivateField(handler, "_timeoutCount", 5L);
        SetPrivateField(handler, "_matchedCount", 5L);

        InvokePrivate(handler, "TryLogTimeoutRateAlert");
        InvokePrivate(handler, "TryLogWaitBacklogAlert", 3L);
        InvokePrivate(handler, "TryLogWaitBacklogAlert", 5L);

        var errorLogs = logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Contains(errorLogs, m => m.Contains("Timeout rate alert"));
        Assert.Equal(1, errorLogs.Count(m => m.Contains("Wait backlog alert")));
    }

    [Fact]
    public void AddSerialPortService_WhenConfigInvalid_ShouldNormalizeAlertOptions()
    {
        var dict = new Dictionary<string, string?>
        {
            ["SerialPortService:GenericHandlerOptions:TimeoutRateAlertThresholdPercent"] = "-1",
            ["SerialPortService:GenericHandlerOptions:TimeoutRateAlertMinSamples"] = "0",
            ["SerialPortService:GenericHandlerOptions:WaitBacklogAlertThreshold"] = "-10",
            ["SerialPortService:GenericHandlerOptions:ReconnectFailureRateAlertThresholdPercent"] = "101",
            ["SerialPortService:GenericHandlerOptions:ReconnectFailureRateAlertMinSamples"] = "0"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();

        services.AddSerialPortService(configuration);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GenericHandlerOptions>();

        Assert.Equal(20, options.TimeoutRateAlertThresholdPercent);
        Assert.Equal(20, options.TimeoutRateAlertMinSamples);
        Assert.Equal(1024, options.WaitBacklogAlertThreshold);
        Assert.Equal(30, options.ReconnectFailureRateAlertThresholdPercent);
        Assert.Equal(20, options.ReconnectFailureRateAlertMinSamples);
    }

    [Fact]
    public void AddSerialPortService_WhenCalledTwiceWithConfig_ShouldApplyLatestConfiguredOptions()
    {
        var services = new ServiceCollection();
        services.AddSerialPortService();

        var dict = new Dictionary<string, string?>
        {
            ["SerialPortService:GenericHandlerOptions:TimeoutRateAlertThresholdPercent"] = "35"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        services.AddSerialPortService(configuration);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GenericHandlerOptions>();

        Assert.Equal(35, options.TimeoutRateAlertThresholdPercent);
    }

    [Fact]
    public void OpenPort_WhenReopenSamePortWithDifferentSettings_ShouldReturnFailure()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_REOPEN_{Guid.NewGuid():N}";

        try
        {
            var first = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Controller, ProtocolEnum.Default);
            var second = sut.OpenPort(portName, 115200, Parity.None, 8, StopBits.One, HandleEnum.Controller, ProtocolEnum.Default);

            Assert.True(first.IsSuccess);
            Assert.False(second.IsSuccess);
            Assert.Contains("不同参数", second.Message);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    private static void EnsureFactoryRegistered()
    {
        if (Interlocked.CompareExchange(ref _factoryRegistered, 1, 0) != 0)
        {
            return;
        }

        var service = new SerialPortServiceBase();
        service.RegisterHandlerFactory(HandleEnum.Controller, CreateFakePortContext);
    }

    private static IPortContext CreateFakePortContext(
        string portName,
        int baudRate,
        Parity parity,
        int dataBits,
        StopBits stopBits,
        HandleEnum handleEnum,
        ProtocolEnum protocol,
        ILoggerFactory loggerFactory)
    {
        var throwOnSend = portName.Contains("THROW", StringComparison.OrdinalIgnoreCase);
        return new FakePortContext(portName, throwOnSend);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args.Length == 0 ? null : args);
    }

    private sealed class FakePortContext : IPortContext
    {
        private bool _opened;
        private readonly bool _throwOnSend;
        private bool _lastCloseSucceeded = true;

        public FakePortContext(string name, bool throwOnSend)
        {
            Name = name;
            _throwOnSend = throwOnSend;
        }

        public string Name { get; }

        public event EventHandler<object>? OnHandleChanged;

        public void Open() => _opened = true;

        public Task OpenAsync() { _opened = true; return Task.CompletedTask; }

        public void Close()
        {
            _opened = false;
            _lastCloseSucceeded = true;
        }

        public Task<byte[]> Send(byte[] data)
        {
            if (!_opened)
            {
                throw new InvalidOperationException("port not opened");
            }

            if (_throwOnSend)
            {
                throw new InvalidOperationException("simulated send failure");
            }

            return Task.FromResult(data);
        }

        public void Dispose()
        {
            _opened = false;
            _lastCloseSucceeded = true;
        }

        public bool LastCloseSucceeded => _lastCloseSucceeded;
    }

    private sealed class DummyStringParser : IStreamParser<string>
    {
        public bool TryParse(byte b, out string? result)
        {
            result = null;
            return false;
        }

        public void Reset()
        {
        }
    }

    private sealed class TestPortContext : PortContext<string>
    {
        private readonly IStreamParser<string> _parser = new DummyStringParser();

        public TestPortContext(string portName)
            : base(portName, 9600, Parity.None, 8, StopBits.One, new NullLogger())
        {
        }

        protected override IStreamParser<string> Parser => _parser;
    }

    private sealed class TestLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Entries.Enqueue((logLevel, message));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class NullLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
