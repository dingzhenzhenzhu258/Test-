using Microsoft.Extensions.Hosting;

namespace Test_High_speed_acquisition.Services
{
    public sealed class ApplicationWarmupService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => Task.CompletedTask;
    }
}
