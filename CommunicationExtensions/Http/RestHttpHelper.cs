using Newtonsoft.Json;
using RestSharp;

namespace CommunicationExtensions.Http;

/// <summary>
/// 基于 <see cref="RestClient"/> 封装常用 REST 请求能力。
/// </summary>
public sealed class RestHttpHelper
{
    private readonly RestClient _client;

    /// <summary>
    /// 初始化 <see cref="RestHttpHelper"/>。
    /// </summary>
    /// <param name="client">已配置的 <see cref="RestClient"/> 实例。</param>
    public RestHttpHelper(RestClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 发送 GET 请求并将响应 JSON 反序列化为目标类型。
    /// </summary>
    /// <typeparam name="T">目标响应模型类型。</typeparam>
    /// <param name="resource">相对资源路径。</param>
    /// <param name="headers">可选请求头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应为空时返回 <c>default</c>，否则返回反序列化后的对象。</returns>
    public async Task<T?> GetAsync<T>(string resource, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var request = new RestRequest(resource, Method.Get);
        AddHeaders(request, headers);
        return await ExecuteAndDeserializeAsync<T>(request, cancellationToken);
    }

    /// <summary>
    /// 发送包含 JSON 请求体的 POST 请求，并将响应反序列化为目标类型。
    /// </summary>
    /// <typeparam name="TRequest">请求模型类型。</typeparam>
    /// <typeparam name="TResponse">响应模型类型。</typeparam>
    /// <param name="resource">相对资源路径。</param>
    /// <param name="payload">JSON 请求体对象。</param>
    /// <param name="headers">可选请求头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应为空时返回 <c>default</c>，否则返回反序列化后的对象。</returns>
    public async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        string resource,
        TRequest payload,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where TRequest : class
    {
        var request = new RestRequest(resource, Method.Post);
        AddHeaders(request, headers);
        request.AddJsonBody(payload);

        return await ExecuteAndDeserializeAsync<TResponse>(request, cancellationToken);
    }

    /// <summary>
    /// 发送包含 JSON 请求体的 PUT 请求，并将响应反序列化为目标类型。
    /// </summary>
    /// <typeparam name="TRequest">请求模型类型。</typeparam>
    /// <typeparam name="TResponse">响应模型类型。</typeparam>
    /// <param name="resource">相对资源路径。</param>
    /// <param name="payload">JSON 请求体对象。</param>
    /// <param name="headers">可选请求头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应为空时返回 <c>default</c>，否则返回反序列化后的对象。</returns>
    public async Task<TResponse?> PutJsonAsync<TRequest, TResponse>(
        string resource,
        TRequest payload,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where TRequest : class
    {
        var request = new RestRequest(resource, Method.Put);
        AddHeaders(request, headers);
        request.AddJsonBody(payload);

        return await ExecuteAndDeserializeAsync<TResponse>(request, cancellationToken);
    }

    /// <summary>
    /// 发送包含 JSON 请求体的 PATCH 请求，并将响应反序列化为目标类型。
    /// </summary>
    /// <typeparam name="TRequest">请求模型类型。</typeparam>
    /// <typeparam name="TResponse">响应模型类型。</typeparam>
    /// <param name="resource">相对资源路径。</param>
    /// <param name="payload">JSON 请求体对象。</param>
    /// <param name="headers">可选请求头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应为空时返回 <c>default</c>，否则返回反序列化后的对象。</returns>
    public async Task<TResponse?> PatchJsonAsync<TRequest, TResponse>(
        string resource,
        TRequest payload,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        where TRequest : class
    {
        var request = new RestRequest(resource, Method.Patch);
        AddHeaders(request, headers);
        request.AddJsonBody(payload);

        return await ExecuteAndDeserializeAsync<TResponse>(request, cancellationToken);
    }

    /// <summary>
    /// 发送 DELETE 请求并将响应 JSON 反序列化为目标类型。
    /// </summary>
    /// <typeparam name="TResponse">响应模型类型。</typeparam>
    /// <param name="resource">相对资源路径。</param>
    /// <param name="headers">可选请求头。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应为空时返回 <c>default</c>，否则返回反序列化后的对象。</returns>
    public async Task<TResponse?> DeleteAsync<TResponse>(
        string resource,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        var request = new RestRequest(resource, Method.Delete);
        AddHeaders(request, headers);

        return await ExecuteAndDeserializeAsync<TResponse>(request, cancellationToken);
    }

    /// <summary>
    /// 执行请求并将响应 JSON 反序列化。
    /// </summary>
    /// <typeparam name="T">响应模型类型。</typeparam>
    /// <param name="request">已构建的请求对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应为空时返回 <c>default</c>，否则返回反序列化后的对象。</returns>
    private async Task<T?> ExecuteAndDeserializeAsync<T>(RestRequest request, CancellationToken cancellationToken)
    {
        var response = await _client.ExecuteAsync(request, cancellationToken);
        EnsureSuccess(response);

        return string.IsNullOrWhiteSpace(response.Content)
            ? default
            : JsonConvert.DeserializeObject<T>(response.Content);
    }

    /// <summary>
    /// 当提供请求头时，将其添加到请求中。
    /// </summary>
    private static void AddHeaders(RestRequest request, IDictionary<string, string>? headers)
    {
        if (headers == null)
        {
            return;
        }

        foreach (var header in headers)
        {
            request.AddHeader(header.Key, header.Value);
        }
    }

    /// <summary>
    /// 当 HTTP 响应失败时抛出异常。
    /// </summary>
    private static void EnsureSuccess(RestResponse response)
    {
        if (response.IsSuccessful)
        {
            return;
        }

        throw new InvalidOperationException($"HTTP request failed. StatusCode={(int)response.StatusCode}, Message={response.ErrorMessage ?? response.Content}");
    }
}
