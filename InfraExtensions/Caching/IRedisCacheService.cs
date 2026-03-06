namespace InfraExtensions.Caching;

/// <summary>
/// 提供 Redis 常用缓存能力的统一抽象，屏蔽底层序列化与连接细节。
/// </summary>
public interface IRedisCacheService
{
    /// <summary>
    /// 读取缓存；若不存在则通过工厂方法创建并写入缓存后返回。
    /// </summary>
    Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiry) where T : class;

    /// <summary>
    /// 读取指定键并反序列化为目标类型。
    /// </summary>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// 设置缓存值，可选过期时间。
    /// </summary>
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null);

    /// <summary>
    /// 判断键是否存在。
    /// </summary>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// 更新键过期时间。
    /// </summary>
    Task<bool> ExpireAsync(string key, TimeSpan expiry);

    /// <summary>
    /// 删除单个键。
    /// </summary>
    Task<bool> DeleteAsync(string key);

    /// <summary>
    /// 批量删除键。
    /// </summary>
    Task<long> DeleteAsync(IEnumerable<string> keys);

    /// <summary>
    /// 按模式删除键（谨慎用于大 keyspace）。
    /// </summary>
    Task<long> DeleteByPatternAsync(string pattern);

    /// <summary>
    /// 对字符串值执行自增操作。
    /// </summary>
    Task<long> IncrementAsync(string key, long value = 1, TimeSpan? expiry = null);

    /// <summary>
    /// 对字符串值执行自减操作。
    /// </summary>
    Task<long> DecrementAsync(string key, long value = 1, TimeSpan? expiry = null);

    /// <summary>
    /// 读取 Hash 字段并反序列化为目标类型。
    /// </summary>
    Task<T?> HashGetAsync<T>(string key, string field);

    /// <summary>
    /// 设置 Hash 字段。
    /// </summary>
    Task<bool> HashSetAsync<T>(string key, string field, T value);

    /// <summary>
    /// 删除 Hash 字段。
    /// </summary>
    Task<bool> HashDeleteAsync(string key, string field);

    /// <summary>
    /// 向 Set 添加元素。
    /// </summary>
    Task<bool> SetAddAsync<T>(string key, T value);

    /// <summary>
    /// 从 Set 删除元素。
    /// </summary>
    Task<bool> SetRemoveAsync<T>(string key, T value);

    /// <summary>
    /// 获取 Set 全量成员并反序列化为目标类型集合。
    /// </summary>
    Task<IReadOnlyCollection<T>> SetMembersAsync<T>(string key);
}
