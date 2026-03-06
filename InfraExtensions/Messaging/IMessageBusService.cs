namespace InfraExtensions.Messaging;

/// <summary>
/// 提供 MassTransit 常用消息操作（发布、发送、请求-响应）的统一抽象。
/// </summary>
public interface IMessageBusService
{
    /// <summary>
    /// 发布事件消息到总线。
    /// </summary>
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// 发送命令到指定终结点。
    /// </summary>
    Task SendAsync<T>(Uri endpointAddress, T message, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// 执行请求-响应模式并返回响应消息体。
    /// </summary>
    Task<TResponse> RequestAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class;
}
