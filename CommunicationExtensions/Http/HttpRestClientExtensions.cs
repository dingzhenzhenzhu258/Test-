using Microsoft.Extensions.DependencyInjection;
using RestSharp;

namespace CommunicationExtensions.Http;

/// <summary>
/// <see cref="RestHttpHelper"/> 的依赖注入扩展方法。
/// </summary>
public static class HttpRestClientExtensions
{
    /// <summary>
    /// 注册一个按键区分（Keyed）的 <see cref="RestHttpHelper"/>。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="key">用于解析该 REST 客户端的服务键。</param>
    /// <param name="apiUrl">接口基础地址。</param>
    /// <returns>当前 <see cref="IServiceCollection"/>，便于链式调用。</returns>
    public static IServiceCollection AddHttpRestClient(this IServiceCollection services, string key, string apiUrl)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Service key is required.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new ArgumentException("API url is required.", nameof(apiUrl));
        }

        services.AddKeyedSingleton<RestHttpHelper>(key, (_, _) =>
        {
            var client = new RestClient(apiUrl);
            return new RestHttpHelper(client);
        });

        return services;
    }
}
