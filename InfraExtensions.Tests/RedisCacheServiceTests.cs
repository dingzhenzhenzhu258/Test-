using InfraExtensions.Caching;
using InfraExtensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
using Xunit;

namespace InfraExtensions.Tests;

public class RedisCacheServiceTests
{
    [Fact]
    public async Task SetAsync_UsesPrefixedKey()
    {
        var database = new Mock<IDatabase>();

        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var service = new RedisCacheService(
            multiplexer.Object,
            new RedisOptions { KeyPrefix = "apiA:" },
            NullLogger<RedisCacheService>.Instance);

        await service.SetAsync("counter", 1);

        var invocation = Assert.Single(database.Invocations, x => x.Method.Name == nameof(IDatabase.StringSetAsync));
        var keyArg = Assert.IsType<RedisKey>(invocation.Arguments[0]);
        Assert.Equal("apiA:counter", keyArg.ToString());
    }

    [Fact]
    public async Task GetAsync_UsesPrefixedKey()
    {
        var payload = JsonSerializer.Serialize(new TestPayload { Name = "ok" });

        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.StringGetAsync(
                It.Is<RedisKey>(k => k == "apiA:item"),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(payload);

        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var service = new RedisCacheService(
            multiplexer.Object,
            new RedisOptions { KeyPrefix = "apiA:" },
            NullLogger<RedisCacheService>.Instance);

        var result = await service.GetAsync<TestPayload>("item");

        Assert.NotNull(result);
        Assert.Equal("ok", result.Name);
        database.VerifyAll();
    }

    private sealed class TestPayload
    {
        public string Name { get; set; } = string.Empty;
    }
}
