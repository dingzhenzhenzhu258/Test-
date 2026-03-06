namespace InfraExtensions.Options;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public List<string>? ClusterNodes { get; set; }
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public bool UseSsl { get; set; } = false;
    public string? SslServerName { get; set; }
    public ushort HeartbeatSeconds { get; set; } = 30;
    public ushort PrefetchCount { get; set; } = 32;
    public int ConcurrentMessageLimit { get; set; } = 16;
    public bool UseInMemoryOutbox { get; set; } = true;
    public bool UseMessageRetry { get; set; } = true;
    public int RetryCount { get; set; } = 3;
    public int RetryIntervalSeconds { get; set; } = 2;
}
