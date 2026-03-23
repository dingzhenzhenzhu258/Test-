using Logger.Extensions.WebAPI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InfraExtensions.Tests;

public sealed class LoggerWebApiMiddlewareTests
{
    [Fact]
    public async Task GlobalExceptionMiddleware_ShouldHideExceptionDetailByDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("secret-detail"),
            new TestLogger<GlobalExceptionMiddleware>(),
            configuration,
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync();

        using var document = JsonDocument.Parse(json);
        Assert.Equal(500, document.RootElement.GetProperty("StatusCode").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("ErrorDetail", out _));
    }

    [Fact]
    public async Task GlobalExceptionMiddleware_WhenConfigured_ShouldIncludeExceptionDetail()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logger:WebApi:IncludeExceptionDetails"] = "true"
            })
            .Build();

        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("secret-detail"),
            new TestLogger<GlobalExceptionMiddleware>(),
            configuration,
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var json = await reader.ReadToEndAsync();

        using var document = JsonDocument.Parse(json);
        Assert.Equal("secret-detail", document.RootElement.GetProperty("ErrorDetail").GetString());
    }

    [Fact]
    public async Task RequestResponseLoggingMiddleware_ShouldDisableBodyLoggingByDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var logger = new TestLogger<RequestResponseLoggingMiddleware>();
        var middleware = new RequestResponseLoggingMiddleware(
            async context =>
            {
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync("{\"ok\":true}");
            },
            logger,
            configuration);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/test";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"password\":\"123\"}"));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Contains("(Disabled)", logger.LastMessage);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Assert.Equal("{\"ok\":true}", body);
    }

    [Fact]
    public async Task RequestResponseLoggingMiddleware_WhenEnabledAndTooLarge_ShouldSkipLargeBodies()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logger:WebApi:LogRequestBody"] = "true",
                ["Logger:WebApi:LogResponseBody"] = "true",
                ["Logger:WebApi:MaxBodyLogBytes"] = "8"
            })
            .Build();

        var logger = new TestLogger<RequestResponseLoggingMiddleware>();
        var middleware = new RequestResponseLoggingMiddleware(
            async context =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"value\":123456}");
            },
            logger,
            configuration);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/test";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = 18;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"value\":123456}"));
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.DoesNotContain("{\"value\":123456}", logger.LastMessage);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = default!;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public string LastMessage { get; private set; } = string.Empty;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastMessage = formatter(state, exception);
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
