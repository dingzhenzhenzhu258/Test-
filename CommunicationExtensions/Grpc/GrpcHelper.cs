using Grpc.Net.Client;

namespace CommunicationExtensions.Grpc;

public static class GrpcHelper
{
    public static GrpcChannel CreateChannel(Uri address)
    {
        if (address == null)
        {
            throw new ArgumentNullException(nameof(address));
        }

        return GrpcChannel.ForAddress(address);
    }

    public static GrpcChannel CreateChannel(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("gRPC address is required.", nameof(address));
        }

        return GrpcChannel.ForAddress(address);
    }

    public static async Task<TResponse> UnaryCallAsync<TClient, TRequest, TResponse>(
        GrpcChannel channel,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, TRequest, CancellationToken, Task<TResponse>> call,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        if (channel == null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        if (clientFactory == null)
        {
            throw new ArgumentNullException(nameof(clientFactory));
        }

        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        var client = clientFactory(channel);
        return await call(client, request, cancellationToken);
    }

    public static async Task<TResponse> UnaryCallAsync<TClient, TRequest, TResponse>(
        string address,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, TRequest, CancellationToken, Task<TResponse>> call,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        if (clientFactory == null)
        {
            throw new ArgumentNullException(nameof(clientFactory));
        }

        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        using var channel = CreateChannel(address);
        return await UnaryCallAsync(channel, clientFactory, call, request, cancellationToken);
    }

    public static async Task<TResponse> UnaryCallWithTimeoutAsync<TClient, TRequest, TResponse>(
        string address,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, TRequest, CancellationToken, Task<TResponse>> call,
        TRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        return await UnaryCallAsync(address, clientFactory, call, request, cts.Token);
    }

    public static async Task<IReadOnlyList<TResponse>> ServerStreamingCallAsync<TClient, TRequest, TResponse>(
        GrpcChannel channel,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, TRequest, CancellationToken, IAsyncEnumerable<TResponse>> call,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        if (channel == null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        if (clientFactory == null)
        {
            throw new ArgumentNullException(nameof(clientFactory));
        }

        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        var client = clientFactory(channel);
        var responseStream = call(client, request, cancellationToken);
        var responses = new List<TResponse>();

        await foreach (var item in responseStream.WithCancellation(cancellationToken))
        {
            responses.Add(item);
        }

        return responses;
    }

    public static async Task<IReadOnlyList<TResponse>> ServerStreamingCallAsync<TClient, TRequest, TResponse>(
        string address,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, TRequest, CancellationToken, IAsyncEnumerable<TResponse>> call,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        using var channel = CreateChannel(address);
        return await ServerStreamingCallAsync(channel, clientFactory, call, request, cancellationToken);
    }

    public static async Task<TResponse> ClientStreamingCallAsync<TClient, TRequest, TResponse>(
        GrpcChannel channel,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, IAsyncEnumerable<TRequest>, CancellationToken, Task<TResponse>> call,
        IAsyncEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (channel == null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        if (clientFactory == null)
        {
            throw new ArgumentNullException(nameof(clientFactory));
        }

        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        if (requests == null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var client = clientFactory(channel);
        return await call(client, requests, cancellationToken);
    }

    public static async Task<TResponse> ClientStreamingCallAsync<TClient, TRequest, TResponse>(
        string address,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, IAsyncEnumerable<TRequest>, CancellationToken, Task<TResponse>> call,
        IAsyncEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        using var channel = CreateChannel(address);
        return await ClientStreamingCallAsync(channel, clientFactory, call, requests, cancellationToken);
    }

    public static async Task<IReadOnlyList<TResponse>> DuplexStreamingCallAsync<TClient, TRequest, TResponse>(
        GrpcChannel channel,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, IAsyncEnumerable<TRequest>, CancellationToken, IAsyncEnumerable<TResponse>> call,
        IAsyncEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (channel == null)
        {
            throw new ArgumentNullException(nameof(channel));
        }

        if (clientFactory == null)
        {
            throw new ArgumentNullException(nameof(clientFactory));
        }

        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        if (requests == null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        var client = clientFactory(channel);
        var responseStream = call(client, requests, cancellationToken);
        var responses = new List<TResponse>();

        await foreach (var item in responseStream.WithCancellation(cancellationToken))
        {
            responses.Add(item);
        }

        return responses;
    }

    public static async Task<IReadOnlyList<TResponse>> DuplexStreamingCallAsync<TClient, TRequest, TResponse>(
        string address,
        Func<GrpcChannel, TClient> clientFactory,
        Func<TClient, IAsyncEnumerable<TRequest>, CancellationToken, IAsyncEnumerable<TResponse>> call,
        IAsyncEnumerable<TRequest> requests,
        CancellationToken cancellationToken = default)
    {
        using var channel = CreateChannel(address);
        return await DuplexStreamingCallAsync(channel, clientFactory, call, requests, cancellationToken);
    }
}
