using InfraExtensions.Caching;
using InfraExtensions.Messaging;
using InfraExtensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfraExtensions.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void BindOptionsWithDefaults_UsesPrefixedSection_WhenExists()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiA:Redis:ConnectionString"] = "10.0.0.1:6379",
                ["Redis:ConnectionString"] = "127.0.0.1:6379"
            })
            .Build();

        var options = config.BindOptionsWithDefaults<RedisOptions>("Redis", "ApiA");

        Assert.Equal("10.0.0.1:6379", options.ConnectionString);
    }

    [Fact]
    public void BindOptionsWithDefaults_FallsBackToRootSection_WhenPrefixedMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "127.0.0.1:6379"
            })
            .Build();

        var options = config.BindOptionsWithDefaults<RedisOptions>("Redis", "ApiA");

        Assert.Equal("127.0.0.1:6379", options.ConnectionString);
    }

    [Fact]
    public void AddInfraDefaults_DoesNotRegisterRedisOrMessageBus_WhenBothDisabled()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Infra:EnableRedis"] = "false",
                ["Infra:EnableMessageBus"] = "false"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddInfraDefaults(config);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IRedisCacheService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IMessageBusService));
    }

    [Fact]
    public void AddRedisDefaults_NormalizesKeyPrefix_ToEndWithColon()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "127.0.0.1:6379",
                ["Redis:KeyPrefix"] = "apiA"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddRedisDefaults(config);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<RedisOptions>();

        Assert.Equal("apiA:", options.KeyPrefix);
    }
}
