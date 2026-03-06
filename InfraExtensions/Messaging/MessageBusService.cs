using MassTransit;

namespace InfraExtensions.Messaging;

/// <summary>
/// MassTransit 消息操作服务实现。
/// </summary>
public sealed class MessageBusService : IMessageBusService
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly IClientFactory _clientFactory;

    public MessageBusService(
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IClientFactory clientFactory)
    {
        _publishEndpoint = publishEndpoint;
        _sendEndpointProvider = sendEndpointProvider;
        _clientFactory = clientFactory;
    }

    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
    {
        return _publishEndpoint.Publish(message, cancellationToken);
    }

    public async Task SendAsync<T>(Uri endpointAddress, T message, CancellationToken cancellationToken = default) where T : class
    {
        var endpoint = await _sendEndpointProvider.GetSendEndpoint(endpointAddress);
        await endpoint.Send(message, cancellationToken);
    }

    public async Task<TResponse> RequestAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class
    {
        var client = _clientFactory.CreateRequestClient<TRequest>();
        var response = await client.GetResponse<TResponse>(request, cancellationToken);
        return response.Message;
    }
}
