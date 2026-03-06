namespace InfraExtensions.Options;

public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public string? KeyPrefix { get; set; }
    public bool AbortOnConnectFail { get; set; } = false;
    public int ConnectTimeout { get; set; } = 5000;
    public int SyncTimeout { get; set; } = 5000;
    public int AsyncTimeout { get; set; } = 5000;
    public int ConnectRetry { get; set; } = 3;
    public int KeepAlive { get; set; } = 60;
    public int? DefaultDatabase { get; set; }
    public bool Ssl { get; set; } = false;
    public bool AllowAdmin { get; set; } = false;
}
