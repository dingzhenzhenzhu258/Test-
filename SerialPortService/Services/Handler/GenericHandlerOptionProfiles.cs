using System.Threading.Channels;

namespace SerialPortService.Services.Handler
{
    public static class GenericHandlerOptionProfiles
    {
        public static GenericHandlerOptions Create(BackpressureProfile profile)
            => profile switch
            {
                BackpressureProfile.Throughput => new GenericHandlerOptions
                {
                    ResponseChannelCapacity = 4096,
                    WaitModeQueueCapacity = 8192,
                    SendChannelCapacity = 2048,
                    RawInputChannelCapacity = 4096,
                    ResponseChannelFullMode = BoundedChannelFullMode.DropOldest,
                    ParsedEventChannelCapacity = 2048,
                    ParsedEventChannelFullMode = BoundedChannelFullMode.DropOldest,
                    WaitBacklogAlertThreshold = 2048,
                    DispatchParsedEventAsync = true
                },
                BackpressureProfile.Reliability => new GenericHandlerOptions
                {
                    ResponseChannelCapacity = 8192,
                    WaitModeQueueCapacity = 32768,
                    SendChannelCapacity = 4096,
                    RawInputChannelCapacity = 8192,
                    ResponseChannelFullMode = BoundedChannelFullMode.Wait,
                    ParsedEventChannelCapacity = 4096,
                    ParsedEventChannelFullMode = BoundedChannelFullMode.Wait,
                    WaitBacklogAlertThreshold = 10000,
                    DispatchParsedEventAsync = true
                },
                BackpressureProfile.LowMemory => new GenericHandlerOptions
                {
                    ResponseChannelCapacity = 256,
                    WaitModeQueueCapacity = 512,
                    SendChannelCapacity = 256,
                    RawInputChannelCapacity = 256,
                    ResponseChannelFullMode = BoundedChannelFullMode.DropOldest,
                    ParsedEventChannelCapacity = 256,
                    ParsedEventChannelFullMode = BoundedChannelFullMode.DropOldest,
                    WaitBacklogAlertThreshold = 256,
                    DispatchParsedEventAsync = true,
                    RawReadBufferSize = 2048
                },
                _ => new GenericHandlerOptions
                {
                    ResponseChannelCapacity = 1024,
                    WaitModeQueueCapacity = 4096,
                    SendChannelCapacity = 1024,
                    RawInputChannelCapacity = 1024,
                    ResponseChannelFullMode = BoundedChannelFullMode.Wait,
                    ParsedEventChannelCapacity = 1024,
                    ParsedEventChannelFullMode = BoundedChannelFullMode.DropOldest,
                    WaitBacklogAlertThreshold = 1024,
                    DispatchParsedEventAsync = true
                }
            };
    }
}

