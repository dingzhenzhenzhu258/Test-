namespace SerialPortService.Services.Handler
{
    public enum BackpressureProfile
    {
        Balanced = 0,
        Throughput = 1,
        Reliability = 2,
        LowMemory = 3
    }
}

