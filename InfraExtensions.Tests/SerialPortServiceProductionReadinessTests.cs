using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Extensions;
using SerialPortService.Services;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols;
using SerialPortService.Services.Protocols.Custom;
using SerialPortService.Services.Protocols.Modbus;
using SerialPortService.Options;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace InfraExtensions.Tests;

public class SerialPortServiceProductionReadinessTests
{
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
            Assert.True(sut.RegisterContextRegistration("010_test_throw", new ThrowingRegistration()).IsSuccess);
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);
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
            Assert.True(sut.RegisterContextRegistration("010_test_ok", new StableRegistration()).IsSuccess);
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);
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
            Assert.Empty(sut.OpenedPorts);
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
            ["SerialPortService:GenericHandlerOptions:SendChannelCapacity"] = "0",
            ["SerialPortService:GenericHandlerOptions:RawInputChannelCapacity"] = "-1",
            ["SerialPortService:GenericHandlerOptions:RawReadBufferSize"] = "0",
            ["SerialPortService:GenericHandlerOptions:SerialPortReadBufferSize"] = "-1",
            ["SerialPortService:GenericHandlerOptions:RawBytesLogIntervalSeconds"] = "0",
            ["SerialPortService:GenericHandlerOptions:ParsedEventChannelCapacity"] = "0",
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
        Assert.Equal(512, options.SendChannelCapacity);
        Assert.Equal(500, options.RawInputChannelCapacity);
        Assert.Equal(4096, options.RawReadBufferSize);
        Assert.Equal(1024 * 1024, options.SerialPortReadBufferSize);
        Assert.Equal(60, options.RawBytesLogIntervalSeconds);
        Assert.Equal(1024, options.ParsedEventChannelCapacity);
        Assert.Equal(System.Threading.Channels.BoundedChannelFullMode.DropOldest, options.ParsedEventChannelFullMode);
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
    public void AddSerialPortService_WhenUsingLegacyGenericHandlerSection_ShouldIgnoreLegacySection()
    {
        var dict = new Dictionary<string, string?>
        {
            ["SerialPortService:GenericHandler:ResponseChannelCapacity"] = "2048",
            ["SerialPortService:GenericHandler:WaitModeQueueCapacity"] = "4096",
            ["SerialPortService:GenericHandler:SendChannelCapacity"] = "2049",
            ["SerialPortService:GenericHandler:RawInputChannelCapacity"] = "2050",
            ["SerialPortService:GenericHandler:EnableRawReadChunkLog"] = "true",
            ["SerialPortService:GenericHandler:DispatchParsedEventAsync"] = "false",
            ["SerialPortService:GenericHandler:ParsedEventChannelCapacity"] = "2051",
            ["SerialPortService:GenericHandler:ParsedEventChannelFullMode"] = "Wait"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();

        services.AddSerialPortService(configuration);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<GenericHandlerOptions>();

        Assert.Equal(new GenericHandlerOptions().ResponseChannelCapacity, options.ResponseChannelCapacity);
        Assert.Equal(new GenericHandlerOptions().WaitModeQueueCapacity, options.WaitModeQueueCapacity);
        Assert.Equal(new GenericHandlerOptions().SendChannelCapacity, options.SendChannelCapacity);
        Assert.Equal(new GenericHandlerOptions().RawInputChannelCapacity, options.RawInputChannelCapacity);
        Assert.Equal(new GenericHandlerOptions().EnableRawReadChunkLog, options.EnableRawReadChunkLog);
        Assert.Equal(new GenericHandlerOptions().DispatchParsedEventAsync, options.DispatchParsedEventAsync);
        Assert.Equal(new GenericHandlerOptions().ParsedEventChannelCapacity, options.ParsedEventChannelCapacity);
        Assert.Equal(new GenericHandlerOptions().ParsedEventChannelFullMode, options.ParsedEventChannelFullMode);
    }

    [Fact]
    public void AddSerialPortService_ShouldBindRequestDefaults()
    {
        var dict = new Dictionary<string, string?>
        {
            ["SerialPortService:RequestDefaults:TimeoutMs"] = "1500",
            ["SerialPortService:RequestDefaults:RetryCount"] = "2"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var services = new ServiceCollection();

        services.AddSerialPortService(configuration);
        var provider = services.BuildServiceProvider();
        var requestDefaults = provider.GetRequiredService<RequestDefaultsOptions>();

        Assert.Equal(1500, requestDefaults.TimeoutMs);
        Assert.Equal(2, requestDefaults.RetryCount);
    }

    [Fact]
    public void OpenPort_WhenReopenSamePortWithDifferentSettings_ShouldReturnFailure()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_REOPEN_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("010_test_reopen", new StableRegistration()).IsSuccess);
            var first = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);
            var second = sut.OpenPort(portName, 115200, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);

            Assert.True(first.IsSuccess);
            Assert.False(second.IsSuccess);
            Assert.Contains("不同参数", second.Message);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void OpenPort_WhenProtocolIsModbusAscii_ShouldReturnNotSupported()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_ASCII_{Guid.NewGuid():N}";

        try
        {
            var result = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);

            Assert.False(result.IsSuccess);
            Assert.Contains("ModbusASCII", result.Message);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void OpenPort_WhenHandleIsController_ShouldUseBuiltInControllerHandler()
    {
        var factoryType = typeof(SerialPortServiceBase).Assembly.GetType("SerialPortService.Services.PortContextFactory", throwOnError: true);
        Assert.NotNull(factoryType);

        var parserRegistry = new ParserFactory();
        var factory = Activator.CreateInstance(
            factoryType!,
            new object[] { new LoggerFactory(), new GenericHandlerOptions(), parserRegistry });
        Assert.NotNull(factory);

        var createMethod = factoryType!.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(createMethod);

        var createResult = createMethod!.Invoke(factory, new object[]
        {
            "COM_CONTROLLER_TEST",
            9600,
            Parity.None,
            8,
            StopBits.One,
            HandleEnum.Controller,
            ProtocolEnum.Default
        });

        Assert.NotNull(createResult);

        var contextField = createResult!.GetType().GetField("Item1");
        Assert.NotNull(contextField);

        var context = contextField!.GetValue(createResult);
        Assert.IsType<ControllerHandler>(context);

        (context as IDisposable)?.Dispose();
    }

    [Fact]
    public void PortContextFactory_WhenHandleIsServoMotor_ShouldUseModbusHandler()
    {
        var factoryType = typeof(SerialPortServiceBase).Assembly.GetType("SerialPortService.Services.PortContextFactory", throwOnError: true);
        Assert.NotNull(factoryType);

        var parserRegistry = new ParserFactory();
        var factory = Activator.CreateInstance(
            factoryType!,
            new object[] { new LoggerFactory(), new GenericHandlerOptions(), parserRegistry });
        Assert.NotNull(factory);

        var createMethod = factoryType!.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(createMethod);

        var createResult = createMethod!.Invoke(factory, new object[]
        {
            "COM_SERVO_TEST",
            9600,
            Parity.None,
            8,
            StopBits.One,
            HandleEnum.ServoMotor,
            ProtocolEnum.Default
        });

        Assert.NotNull(createResult);

        var contextField = createResult!.GetType().GetField("Item1");
        var protocolField = createResult.GetType().GetField("Item2");
        Assert.NotNull(contextField);
        Assert.NotNull(protocolField);

        var context = contextField!.GetValue(createResult);
        var protocol = protocolField!.GetValue(createResult);

        Assert.IsType<ModbusHandler>(context);
        Assert.Equal(ProtocolEnum.ModbusRTU, protocol);

        (context as IDisposable)?.Dispose();
    }

    [Fact]
    public void TemperatureSensorHandler_ShouldUseInjectedParser()
    {
        var parser = new DummyModbusParser();
        var logger = new NullLogger();

        using var handler = new TemperatureSensorHandler(
            "COM_TEMP_TEST",
            9600,
            Parity.None,
            8,
            StopBits.One,
            parser,
            logger);

        var parserField = typeof(GenericHandler<ModbusPacket>).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(parserField);
        Assert.Same(parser, parserField!.GetValue(handler));
    }

    [Fact]
    public void PortContextFactory_WhenHandleIsCustomProtocol_ShouldUseCustomProtocolHandler()
    {
        var factoryType = typeof(SerialPortServiceBase).Assembly.GetType("SerialPortService.Services.PortContextFactory", throwOnError: true);
        Assert.NotNull(factoryType);

        var parserRegistry = new ParserFactory();
        var factory = Activator.CreateInstance(
            factoryType!,
            new object[] { new LoggerFactory(), new GenericHandlerOptions(), parserRegistry });
        Assert.NotNull(factory);

        var createMethod = factoryType!.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(createMethod);

        var createResult = createMethod!.Invoke(factory, new object[]
        {
            "COM_CUSTOM_TEST",
            9600,
            Parity.None,
            8,
            StopBits.One,
            HandleEnum.CustomProtocol,
            ProtocolEnum.Default
        });

        Assert.NotNull(createResult);

        var contextField = createResult!.GetType().GetField("Item1");
        Assert.NotNull(contextField);

        var context = contextField!.GetValue(createResult);
        Assert.IsType<CustomProtocolHandler>(context);

        (context as IDisposable)?.Dispose();
    }

    [Fact]
    public void RegisterContextRegistration_WhenRegistered_ShouldCreateMatchingContext()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_REG_{Guid.NewGuid():N}";

        try
        {
            var registered = sut.RegisterContextRegistration(
                "050_test_registration",
                new TestPortContextRegistration());
            Assert.True(registered.IsSuccess);

            var result = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);
            Assert.True(result.IsSuccess);
            Assert.True(sut.TryGetContext(portName, out var context));
            Assert.IsType<FakePortContext>(context);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void RegisterContextRegistration_WhenMultipleMatch_ShouldUseLowestPriorityKey()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_REG_PRIORITY_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("041_test_priority_high", new PriorityRegistration("HIGH")).IsSuccess);
            Assert.True(sut.RegisterContextRegistration("040_test_priority_low", new PriorityRegistration("LOW")).IsSuccess);

            var result = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);
            Assert.True(result.IsSuccess);
            Assert.True(sut.TryGetContext(portName, out var context));
            Assert.IsType<LabeledFakePortContext>(context);
            Assert.Equal("LOW", ((LabeledFakePortContext)context!).Label);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void RegisterContextRegistration_WhenDuplicateKey_ShouldReturnFailureResult()
    {
        var sut = new SerialPortServiceBase();

        var first = sut.RegisterContextRegistration("040_test_duplicate", new StableRegistration());
        var duplicate = sut.RegisterContextRegistration("040_test_duplicate", new StableRegistration());

        Assert.True(first.IsSuccess);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("040_test_duplicate", duplicate.Key);
        Assert.Equal("040_test_duplicate", duplicate.ExistingKey);
        Assert.Contains("already exists", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterParser_WhenDuplicateProtocolAndResultType_ShouldReturnFailureResult()
    {
        var factory = new ParserFactory();

        var first = factory.Register(ProtocolEnum.ModbusASCII, "ascii_parser_v1", static () => new DummyStringParser());
        var duplicate = factory.Register(ProtocolEnum.ModbusASCII, "ascii_parser_v2", static () => new DummyStringParser());

        Assert.True(first.IsSuccess);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal("ascii_parser_v2", duplicate.Key);
        Assert.Equal("ascii_parser_v1", duplicate.ExistingKey);
        Assert.Contains("already exists", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParserFactory_WhenCustomParserRegistered_ShouldCreateRegisteredParser()
    {
        var factory = new ParserFactory();

        var result = factory.Register(ProtocolEnum.ModbusASCII, "ascii_string_parser", static () => new DummyStringParser());
        var parser = factory.Create<string>(ProtocolEnum.ModbusASCII);

        Assert.True(result.IsSuccess);
        Assert.IsType<DummyStringParser>(parser);
    }

    [Fact]
    public void RegisterProtocolDefinition_WhenDuplicateProtocolAndPacketType_ShouldReturnFailureResult()
    {
        var registry = new ProtocolDefinitionRegistry();

        var duplicate = registry.Register("custom_modbus_duplicate", new ModbusProtocolDefinition());

        Assert.False(duplicate.IsSuccess);
        Assert.Equal("custom_modbus_duplicate", duplicate.Key);
        Assert.Equal("builtin_modbus_rtu", duplicate.ExistingKey);
    }

    [Fact]
    public void SerialPortServiceBase_WhenProtocolDefinitionRegistered_ShouldResolveByProtocol()
    {
        var sut = new SerialPortServiceBase();
        var result = sut.RegisterProtocolDefinition("040_custom_ascii_protocol", new DummyAsciiProtocolDefinition());

        Assert.True(result.IsSuccess);
        Assert.True(sut.TryGetProtocolDefinition<CustomFrame>(ProtocolEnum.ModbusASCII, out var definition));
        Assert.NotNull(definition);
        Assert.Equal("Dummy ASCII", definition!.Name);
        Assert.IsType<DummyCustomFrameParser>(definition.CreateParser());
    }

    [Fact]
    public void PortContextRuntimeSnapshot_ShouldExposeRecentEventsErrorsAndCloseState()
    {
        using var context = new DiagnosticPortContext("COM_DIAG_TEST");

        context.EmitEvent("custom", "event-1");
        context.EmitError("custom", "error-1");
        context.Close();

        var snapshot = context.GetRuntimeSnapshot();

        Assert.Equal(PortCloseState.Completed, snapshot.CloseState);
        Assert.True(snapshot.LastCloseDurationMs >= 0);
        Assert.Contains(snapshot.RecentEvents, x => x.Message.Contains("event-1"));
        Assert.Contains(snapshot.RecentErrors, x => x.Message.Contains("error-1"));
        Assert.Contains(context.GetRecentEvents(), x => x.Message.Contains("close"));
    }

    [Fact]
    public void GenericHandlerOptionProfiles_ShouldProvideDistinctOperationalProfiles()
    {
        var balanced = GenericHandlerOptionProfiles.Create(BackpressureProfile.Balanced);
        var throughput = GenericHandlerOptionProfiles.Create(BackpressureProfile.Throughput);
        var reliability = GenericHandlerOptionProfiles.Create(BackpressureProfile.Reliability);
        var lowMemory = GenericHandlerOptionProfiles.Create(BackpressureProfile.LowMemory);

        Assert.True(throughput.ResponseChannelCapacity > balanced.ResponseChannelCapacity);
        Assert.Equal(System.Threading.Channels.BoundedChannelFullMode.DropOldest, throughput.ResponseChannelFullMode);
        Assert.Equal(System.Threading.Channels.BoundedChannelFullMode.Wait, reliability.ResponseChannelFullMode);
        Assert.True(reliability.WaitModeQueueCapacity > throughput.WaitModeQueueCapacity);
        Assert.True(lowMemory.ResponseChannelCapacity < balanced.ResponseChannelCapacity);
    }

    [Fact]
    public void ServiceHealthSnapshot_ShouldAggregatePortDiagnostics()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_HEALTH_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("040_test_health", new HealthRegistration()).IsSuccess);
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);

            Assert.True(open.IsSuccess);

            var snapshot = sut.GetHealthSnapshot();

            Assert.Equal(1, snapshot.OpenPortCount);
            Assert.Equal(1, snapshot.RunningPortCount);
            Assert.Single(snapshot.Ports);
            Assert.Equal(portName, snapshot.Ports[0].PortName);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void GetPortRuntimeSnapshot_WhenPortSupportsDiagnostics_ShouldReturnSnapshot()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_SNAPSHOT_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("040_test_snapshot", new HealthRegistration()).IsSuccess);
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);

            Assert.True(open.IsSuccess);

            var snapshot = sut.GetPortRuntimeSnapshot(portName);

            Assert.True(snapshot.IsSuccess);
            Assert.NotNull(snapshot.Snapshot);
            Assert.Equal(portName, snapshot.Snapshot!.Value.PortName);
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public async Task RestartPortAsync_WhenPortExists_ShouldCloseAndReopenWithOriginalBinding()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_RESTART_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("040_test_restart", new StableRegistration()).IsSuccess);
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);

            Assert.True(open.IsSuccess);

            var restart = await sut.RestartPortAsync(portName);

            Assert.True(restart.IsSuccess);
            Assert.True(sut.IsOpen(portName));
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public async Task RestartPortAsync_WhenOpenedWithCustomParserFactory_ShouldCloseAndReopen()
    {
        var sut = new TestableSerialPortService();
        var portName = $"COM_RESTART_CUSTOM_{Guid.NewGuid():N}";

        try
        {
            var open = await sut.OpenPortAsync(
                portName,
                9600,
                Parity.None,
                8,
                StopBits.One,
                static () => new DummyStringParser());

            Assert.True(open.IsSuccess);

            var restart = await sut.RestartPortAsync(portName);

            Assert.True(restart.IsSuccess);
            Assert.True(sut.IsOpen(portName));
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public void SerialPortServiceBase_ShouldKeepPortStateIsolatedPerInstance()
    {
        var sutA = new SerialPortServiceBase();
        var sutB = new SerialPortServiceBase();
        var portName = $"COM_INSTANCE_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sutA.RegisterContextRegistration("040_test_instance_a", new StableRegistration()).IsSuccess);
            Assert.True(sutA.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII).IsSuccess);

            Assert.True(sutA.IsOpen(portName));
            Assert.False(sutB.IsOpen(portName));
            Assert.Single(sutA.OpenedPorts);
            Assert.Empty(sutB.OpenedPorts);
        }
        finally
        {
            sutA.CloseAll();
            sutB.CloseAll();
        }
    }

    [Fact]
    public void PortRuntimeSnapshot_WhenRecentErrorsExist_ShouldBeFaulted()
    {
        var snapshot = new PortRuntimeSnapshot(
            "COM_LEVEL_TEST",
            true,
            true,
            true,
            PortCloseState.Completed,
            10,
            100,
            10,
            0,
            0,
            0,
            null,
            0,
            Array.Empty<PortDiagnosticEvent>(),
            new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "error", "boom") });

        Assert.Equal(HealthStatusLevel.Faulted, snapshot.HealthStatus);
    }

    [Fact]
    public void ServiceDiagnosticReport_ShouldAggregateRecentErrors()
    {
        var sut = new SerialPortServiceBase();
        var portName = $"COM_REPORT_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("040_test_report", new ErrorDiagnosticRegistration()).IsSuccess);
            var open = sut.OpenPort(portName, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII);

            Assert.True(open.IsSuccess);

            var report = sut.GetDiagnosticReport();

            Assert.True(report.OpenPortCount >= 1);
            Assert.Contains(report.RecentErrors, x => x.Message.Contains("simulated-error"));
        }
        finally
        {
            sut.CloseAll();
        }
    }

    [Fact]
    public async Task RestartPortsAsync_ShouldReturnBatchSummary()
    {
        var sut = new SerialPortServiceBase();
        var portA = $"COM_BATCH_A_{Guid.NewGuid():N}";
        var portB = $"COM_BATCH_B_{Guid.NewGuid():N}";

        try
        {
            Assert.True(sut.RegisterContextRegistration("040_test_batch_restart", new StableRegistration()).IsSuccess);
            Assert.True(sut.OpenPort(portA, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII).IsSuccess);
            Assert.True(sut.OpenPort(portB, 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusASCII).IsSuccess);

            var batch = await sut.RestartPortsAsync(new[] { portA, portB });

            Assert.Equal(2, batch.SuccessCount);
            Assert.Equal(0, batch.FailureCount);
            Assert.Equal(2, batch.Results.Count);
        }
        finally
        {
            sut.CloseAll();
        }
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

        public event EventHandler<object>? OnHandleChanged
        {
            add { }
            remove { }
        }

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

    private sealed class TestableSerialPortService : SerialPortServiceBase
    {
        protected override IPortContext CreateCustomParserContext<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory, IStreamParser<T>? parser = null)
            where T : class
            => new FakePortContext(portName, throwOnSend: false);
    }

    private sealed class DummyStringParser : IStreamParser<string>
    {
        public bool TryParse(byte b, [NotNullWhen(true)] out string? result)
        {
            result = null;
            return false;
        }

        public void Reset()
        {
        }
    }

    private sealed class ThrowingRegistration : IPortContextRegistration
    {
        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol)
            => handleEnum == HandleEnum.Default && protocol == ProtocolEnum.ModbusASCII;

        public IPortContext Create(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory, GenericHandlerOptions options)
            => new FakePortContext(portName, throwOnSend: true);
    }

    private sealed class StableRegistration : IPortContextRegistration
    {
        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol)
            => handleEnum == HandleEnum.Default && protocol == ProtocolEnum.ModbusASCII;

        public IPortContext Create(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory, GenericHandlerOptions options)
            => new FakePortContext(portName, throwOnSend: false);
    }

    private sealed class TestPortContextRegistration : IPortContextRegistration
    {
        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol)
            => handleEnum == HandleEnum.Default && protocol == ProtocolEnum.ModbusASCII;

        public IPortContext Create(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory, GenericHandlerOptions options)
            => new FakePortContext(portName, throwOnSend: false);
    }

    private sealed class PriorityRegistration : IPortContextRegistration
    {
        private readonly string _label;

        public PriorityRegistration(string label)
        {
            _label = label;
        }

        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol)
            => handleEnum == HandleEnum.Default && protocol == ProtocolEnum.ModbusASCII;

        public IPortContext Create(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory, GenericHandlerOptions options)
            => new LabeledFakePortContext(portName, _label);
    }

    private sealed class HealthRegistration : IPortContextRegistration
    {
        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol)
            => handleEnum == HandleEnum.Default && protocol == ProtocolEnum.ModbusASCII;

        public IPortContext Create(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory, GenericHandlerOptions options)
            => new HealthDiagnosticContext(portName);
    }

    private sealed class ErrorDiagnosticRegistration : IPortContextRegistration
    {
        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol)
            => handleEnum == HandleEnum.Default && protocol == ProtocolEnum.ModbusASCII;

        public IPortContext Create(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory, GenericHandlerOptions options)
            => new ErrorHealthDiagnosticContext(portName);
    }

    private sealed class LabeledFakePortContext : IPortContext
    {
        public LabeledFakePortContext(string name, string label)
        {
            Name = name;
            Label = label;
        }

        public string Label { get; }
        public string Name { get; }
        public event EventHandler<object>? OnHandleChanged { add { } remove { } }
        public bool LastCloseSucceeded => true;
        public void Open() { }
        public Task OpenAsync() => Task.CompletedTask;
        public void Close() { }
        public Task<byte[]> Send(byte[] data) => Task.FromResult(data);
        public void Dispose() { }
    }

    private sealed class DummyModbusParser : IStreamParser<ModbusPacket>
    {
        public bool TryParse(byte b, [NotNullWhen(true)] out ModbusPacket? result)
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

    private sealed class DiagnosticPortContext : PortContext<string>
    {
        private readonly IStreamParser<string> _parser = new DummyStringParser();

        public DiagnosticPortContext(string portName)
            : base(portName, 9600, Parity.None, 8, StopBits.One, new NullLogger())
        {
        }

        protected override IStreamParser<string> Parser => _parser;

        public void EmitEvent(string category, string message) => RecordDiagnosticEvent(category, message);

        public void EmitError(string category, string message) => RecordDiagnosticError(category, message);
    }

    private class HealthDiagnosticContext : IPortContext, IPortRuntimeDiagnostics
    {
        public HealthDiagnosticContext(string portName)
        {
            Name = portName;
        }

        public string Name { get; }
        public bool LastCloseSucceeded => true;
        public event EventHandler<object>? OnHandleChanged { add { } remove { } }
        public void Open() { }
        public Task OpenAsync() => Task.CompletedTask;
        public void Close() { }
        public Task<byte[]> Send(byte[] data) => Task.FromResult(data);
        public void Dispose() { }

        public virtual PortRuntimeSnapshot GetRuntimeSnapshot()
            => new(
                Name,
                true,
                true,
                true,
                PortCloseState.NotStarted,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                0,
                new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "health", "simulated-open") },
                Array.Empty<PortDiagnosticEvent>());

        public virtual IReadOnlyList<PortDiagnosticEvent> GetRecentEvents()
            => new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "health", "simulated-open") };

        public virtual IReadOnlyList<PortDiagnosticEvent> GetRecentErrors()
            => Array.Empty<PortDiagnosticEvent>();
    }

    private sealed class ErrorHealthDiagnosticContext : HealthDiagnosticContext
    {
        public ErrorHealthDiagnosticContext(string portName)
            : base(portName)
        {
        }

        public override PortRuntimeSnapshot GetRuntimeSnapshot()
            => new(
                Name,
                true,
                true,
                true,
                PortCloseState.Completed,
                0,
                0,
                0,
                0,
                0,
                0,
                "simulated-reconnect",
                DateTime.UtcNow.Ticks,
                new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "event", "simulated-event") },
                new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "error", "simulated-error") });

        public override IReadOnlyList<PortDiagnosticEvent> GetRecentEvents()
            => new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "event", "simulated-event") };

        public override IReadOnlyList<PortDiagnosticEvent> GetRecentErrors()
            => new[] { new PortDiagnosticEvent(DateTime.UtcNow.Ticks, "error", "simulated-error") };
    }

    private sealed class DummyAsciiProtocolDefinition : IProtocolDefinition<CustomFrame>
    {
        public string Name => "Dummy ASCII";
        public ProtocolEnum Protocol => ProtocolEnum.ModbusASCII;
        public IStreamParser<CustomFrame> CreateParser() => new DummyCustomFrameParser();
        public IResponseMatcher<CustomFrame> CreateResponseMatcher() => new DummyCustomFrameMatcher();
        public byte[] GetRawFrame(CustomFrame packet) => packet.Raw;
    }

    private sealed class DummyCustomFrameMatcher : IResponseMatcher<CustomFrame>
    {
        public bool IsResponseMatch(CustomFrame response, byte[] command) => true;
        public bool IsReportPacket(CustomFrame response) => false;
        public void OnReportPacket(CustomFrame response) { }
        public string BuildUnmatchedLog(CustomFrame response) => "dummy";
    }

    private sealed class DummyCustomFrameParser : IStreamParser<CustomFrame>
    {
        public bool TryParse(byte b, [NotNullWhen(true)] out CustomFrame? result)
        {
            result = null;
            return false;
        }

        public void Reset()
        {
        }
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
