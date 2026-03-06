using InfraExtensions.Options;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace InfraExtensions.Caching;

/// <summary>
/// 基于 StackExchange.Redis 的缓存服务实现，封装常用字符串/哈希/集合操作。
/// </summary>
public sealed class RedisCacheService : IRedisCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly string _keyPrefix;

    public RedisCacheService(
        IConnectionMultiplexer connection,
        RedisOptions redisOptions,
        ILogger<RedisCacheService> logger)
    {
        _connection = connection;
        _database = connection.GetDatabase();
        _keyPrefix = redisOptions.KeyPrefix ?? string.Empty;
        _logger = logger;
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry) where T : class
    {
        var cached = await GetAsync<T>(key);
        if (cached != null)
        {
            return cached;
        }

        var created = await factory();
        await SetAsync(key, created, expiry);
        return created;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var redisKey = BuildKey(key);
            var value = await _database.StringGetAsync(redisKey);
            if (!value.HasValue)
                return default;

            return JsonSerializer.Deserialize<T>(value!, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis GetAsync failed. key={Key}", BuildKey(key));
            throw;
        }
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        try
        {
            var payload = JsonSerializer.Serialize(value, JsonOptions);
            var redisKey = BuildKey(key);
            return expiry.HasValue
                ? _database.StringSetAsync(redisKey, payload, expiry.Value)
                : _database.StringSetAsync(redisKey, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis SetAsync failed. key={Key}", BuildKey(key));
            throw;
        }
    }

    public Task<bool> ExistsAsync(string key) => _database.KeyExistsAsync(BuildKey(key));

    public Task<bool> ExpireAsync(string key, TimeSpan expiry) => _database.KeyExpireAsync(BuildKey(key), expiry);

    public Task<bool> DeleteAsync(string key) => _database.KeyDeleteAsync(BuildKey(key));

    public Task<long> DeleteAsync(IEnumerable<string> keys)
    {
        var redisKeys = keys.Select(x => (RedisKey)BuildKey(x)).ToArray();
        if (redisKeys.Length == 0)
            return Task.FromResult(0L);

        return _database.KeyDeleteAsync(redisKeys);
    }

    public async Task<long> DeleteByPatternAsync(string pattern)
    {
        long total = 0;

        foreach (var endpoint in _connection.GetEndPoints())
        {
            var server = _connection.GetServer(endpoint);
            if (!server.IsConnected)
                continue;

            var keys = server.Keys(pattern: BuildPattern(pattern)).ToArray();
            if (keys.Length == 0)
                continue;

            total += await _database.KeyDeleteAsync(keys);
        }

        _logger.LogInformation("Redis DeleteByPattern completed. pattern={Pattern}, deleted={Deleted}", BuildPattern(pattern), total);
        return total;
    }

    public async Task<long> IncrementAsync(string key, long value = 1, TimeSpan? expiry = null)
    {
        var redisKey = BuildKey(key);
        var result = await _database.StringIncrementAsync(redisKey, value);
        if (expiry.HasValue)
        {
            await _database.KeyExpireAsync(redisKey, expiry.Value);
        }

        return (long)result;
    }

    public async Task<long> DecrementAsync(string key, long value = 1, TimeSpan? expiry = null)
    {
        var redisKey = BuildKey(key);
        var result = await _database.StringDecrementAsync(redisKey, value);
        if (expiry.HasValue)
        {
            await _database.KeyExpireAsync(redisKey, expiry.Value);
        }

        return (long)result;
    }

    public async Task<T?> HashGetAsync<T>(string key, string field)
    {
        var value = await _database.HashGetAsync(BuildKey(key), field);
        if (!value.HasValue)
            return default;

        return JsonSerializer.Deserialize<T>(value!, JsonOptions);
    }

    public Task<bool> HashSetAsync<T>(string key, string field, T value)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        return _database.HashSetAsync(BuildKey(key), field, payload);
    }

    public Task<bool> HashDeleteAsync(string key, string field) => _database.HashDeleteAsync(BuildKey(key), field);

    public Task<bool> SetAddAsync<T>(string key, T value)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        return _database.SetAddAsync(BuildKey(key), payload);
    }

    public Task<bool> SetRemoveAsync<T>(string key, T value)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        return _database.SetRemoveAsync(BuildKey(key), payload);
    }

    public async Task<IReadOnlyCollection<T>> SetMembersAsync<T>(string key)
    {
        var members = await _database.SetMembersAsync(BuildKey(key));
        if (members.Length == 0)
            return Array.Empty<T>();

        var result = new List<T>(members.Length);
        foreach (var member in members)
        {
            var item = JsonSerializer.Deserialize<T>(member!, JsonOptions);
            if (item != null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private string BuildKey(string key)
    {
        if (string.IsNullOrEmpty(_keyPrefix) || key.StartsWith(_keyPrefix, StringComparison.Ordinal))
        {
            return key;
        }

        return $"{_keyPrefix}{key}";
    }

    private string BuildPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "*";
        }

        if (string.IsNullOrEmpty(_keyPrefix) || pattern.StartsWith(_keyPrefix, StringComparison.Ordinal))
        {
            return pattern;
        }

        return $"{_keyPrefix}{pattern}";
    }
}
