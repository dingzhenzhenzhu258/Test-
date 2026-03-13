using Logger.Helpers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfraExtensions.Tests;

public class LoggerHelperTests
{
    [Fact]
    public void AddLog_WithNamedExceptionAndArgs_ShouldCaptureException()
    {
        var logger = new TestLogger();
        var ex = new InvalidOperationException("test-exception");

        logger.AddLog(LogLevel.Error, "读取设备失败，DeviceId={DeviceId}", exception: ex, args: "D1");

        Assert.Equal(LogLevel.Error, logger.LastLevel);
        Assert.Same(ex, logger.LastException);
    }

    [Fact]
    public void AddLog_WithExceptionInArgs_ShouldNormalizeToExceptionParameter()
    {
        var logger = new TestLogger();
        var ex = new ArgumentException("bad-arg");

        logger.AddLog(LogLevel.Error, "读取设备失败，DeviceId={DeviceId}", args: new object[] { "D1", ex });

        Assert.Equal(LogLevel.Error, logger.LastLevel);
        Assert.Same(ex, logger.LastException);
    }

    [Fact]
    public void AddLog_WithSerilogTemplateAndUiEnabled_ShouldNotThrowFormatException()
    {
        var logger = new TestLogger();

        var exception = Record.Exception(() =>
            logger.AddLog(LogLevel.Information, "读取设备失败，DeviceId={DeviceId}", isShowUI: true, args: "D1"));

        Assert.Null(exception);
    }

    private sealed class TestLogger : ILogger
    {
        public LogLevel LastLevel { get; private set; }
        public Exception? LastException { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastLevel = logLevel;
            LastException = exception;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
