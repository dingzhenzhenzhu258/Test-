using InfraExtensions.Options;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace InfraExtensions;

/// <summary>
/// 基础设施扩展注册入口，统一封装 Redis 与 MassTransit 的常用接入方式。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 一站式注册基础设施默认能力：Redis 连接、多态缓存服务、MassTransit 总线与消息操作服务。
    /// </summary>
    public static IServiceCollection AddInfraDefaults(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? register = null,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureEndpoints = null,
        bool? enableRedis = null,
        bool? enableMessageBus = null,
        string? sectionPrefix = null,
        string redisSectionName = "Redis",
        string rabbitMqSectionName = "RabbitMq",
        Action<RedisOptions>? configureRedisOptions = null,
        Action<RabbitMqOptions>? configureRabbitMqOptions = null)
    {
        var useRedis = enableRedis
            ?? configuration.GetValue<bool?>("Infra:EnableRedis")
            ?? true;

        var useMessageBus = enableMessageBus
            ?? configuration.GetValue<bool?>("Infra:EnableMessageBus")
            ?? true;

        if (useRedis)
        {
            services.AddRedisDefaults(configuration, redisSectionName, sectionPrefix, configureRedisOptions);
            services.AddRedisCommonServices();
        }

        if (useMessageBus)
        {
            services.AddMassTransitDefaults(configuration, register, configureEndpoints, rabbitMqSectionName, sectionPrefix, configureRabbitMqOptions);
            services.AddMessageBusCommonServices();
        }

        return services;
    }

    /// <summary>
    /// 从配置节绑定选项，若配置缺失则回退到选项类型默认值。
    /// </summary>
    public static TOptions BindOptionsWithDefaults<TOptions>(
        this IConfiguration configuration,
        string sectionName,
        string? sectionPrefix = null)
        where TOptions : new()
    {
        if (!string.IsNullOrWhiteSpace(sectionPrefix))
        {
            var prefixedSection = configuration.GetSection($"{sectionPrefix}:{sectionName}");
            if (prefixedSection.Exists())
            {
                return prefixedSection.Get<TOptions>() ?? new TOptions();
            }
        }

        var section = configuration.GetSection(sectionName);
        return section.Exists() ? section.Get<TOptions>() ?? new TOptions() : new TOptions();
    }

    /// <summary>
    /// 注册 Redis 高层缓存服务。
    /// </summary>
    public static IServiceCollection AddRedisCommonServices(this IServiceCollection services)
    {
        services.AddSingleton<Caching.IRedisCacheService, Caching.RedisCacheService>();
        return services;
    }

    /// <summary>
    /// 注册 MassTransit 高频使用消息操作服务。
    /// </summary>
    public static IServiceCollection AddMessageBusCommonServices(this IServiceCollection services)
    {
        // MessageBusService depends on MassTransit scoped services (eg. IPublishEndpoint),
        // so register it as scoped to avoid consuming scoped services from a singleton.
        services.AddScoped<Messaging.IMessageBusService, Messaging.MessageBusService>();
        return services;
    }

    /// <summary>
    /// 注册 Redis 连接多路复用器，并应用常见连接与超时配置。
    /// </summary>
    public static IServiceCollection AddRedisDefaults(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Redis",
        string? sectionPrefix = null,
        Action<RedisOptions>? configureOptions = null)
    {
        var options = configuration.BindOptionsWithDefaults<RedisOptions>(sectionName, sectionPrefix);
        NormalizeRedisOptions(options);
        configureOptions?.Invoke(options);
        NormalizeRedisOptions(options);

        services.AddSingleton<IOptions<RedisOptions>>(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RedisOptions>>().Value);

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var config = ConfigurationOptions.Parse(options.ConnectionString, ignoreUnknown: true);
            config.AbortOnConnectFail = options.AbortOnConnectFail;
            config.ConnectTimeout = options.ConnectTimeout;
            config.SyncTimeout = options.SyncTimeout;
            config.AsyncTimeout = options.AsyncTimeout;
            config.ConnectRetry = options.ConnectRetry;
            config.KeepAlive = options.KeepAlive;
            config.Ssl = options.Ssl;
            config.AllowAdmin = options.AllowAdmin;

            if (options.DefaultDatabase.HasValue)
            {
                config.DefaultDatabase = options.DefaultDatabase.Value;
            }

            return ConnectionMultiplexer.Connect(config);
        });

        return services;
    }

    /// <summary>
    /// 注册 MassTransit + RabbitMQ 默认配置，并支持调用方注入消费者与接收端点定义。
    /// </summary>
    public static IServiceCollection AddMassTransitDefaults(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? register = null,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureEndpoints = null,
        string sectionName = "RabbitMq",
        string? sectionPrefix = null,
        Action<RabbitMqOptions>? configureOptions = null)
    {
        var options = configuration.BindOptionsWithDefaults<RabbitMqOptions>(sectionName, sectionPrefix);
        NormalizeRabbitMqOptions(options);
        configureOptions?.Invoke(options);
        NormalizeRabbitMqOptions(options);

        services.AddSingleton<IOptions<RabbitMqOptions>>(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value);

        services.AddMassTransit(x =>
        {
            register?.Invoke(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.PrefetchCount = options.PrefetchCount;

                cfg.Host(options.Host, options.Port, options.VirtualHost, h =>
                {
                    h.Username(options.Username);
                    h.Password(options.Password);
                    h.Heartbeat(TimeSpan.FromSeconds(options.HeartbeatSeconds));

                    if (options.ClusterNodes is { Count: > 0 })
                    {
                        h.UseCluster(c =>
                        {
                            foreach (var node in options.ClusterNodes)
                            {
                                c.Node(node);
                            }
                        });
                    }

                    if (options.UseSsl)
                    {
                        h.UseSsl(s =>
                        {
                            s.ServerName = string.IsNullOrWhiteSpace(options.SslServerName)
                                ? options.Host
                                : options.SslServerName;
                        });
                    }
                });

                if (options.UseMessageRetry)
                {
                    cfg.UseMessageRetry(r => r.Interval(options.RetryCount, TimeSpan.FromSeconds(options.RetryIntervalSeconds)));
                }

                if (options.UseInMemoryOutbox)
                {
                    cfg.UseInMemoryOutbox(context);
                }

                configureEndpoints?.Invoke(context, cfg);
            });
        });

        return services;
    }

    /// <summary>
    /// 为消费者端点应用默认并发配置并绑定消费者。
    /// </summary>
    public static void ConfigureDefaultConsumerEndpoint<TConsumer>(
        this IRabbitMqReceiveEndpointConfigurator endpoint,
        IBusRegistrationContext context,
        RabbitMqOptions? options = null)
        where TConsumer : class, IConsumer
    {
        if (options != null && options.ConcurrentMessageLimit > 0)
        {
            endpoint.ConcurrentMessageLimit = options.ConcurrentMessageLimit;
        }

        endpoint.ConfigureConsumer<TConsumer>(context);
    }

    private static void NormalizeRedisOptions(RedisOptions options)
    {
        options.ConnectionString = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? "localhost:6379,abortConnect=false"
            : options.ConnectionString;

        if (!string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            options.KeyPrefix = options.KeyPrefix.Trim();
            if (!options.KeyPrefix.EndsWith(':'))
            {
                options.KeyPrefix += ":";
            }
        }

        options.ConnectTimeout = options.ConnectTimeout <= 0 ? 5000 : options.ConnectTimeout;
        options.SyncTimeout = options.SyncTimeout <= 0 ? 5000 : options.SyncTimeout;
        options.AsyncTimeout = options.AsyncTimeout <= 0 ? 5000 : options.AsyncTimeout;
        options.ConnectRetry = options.ConnectRetry <= 0 ? 3 : options.ConnectRetry;
        options.KeepAlive = options.KeepAlive < 0 ? 60 : options.KeepAlive;
    }

    private static void NormalizeRabbitMqOptions(RabbitMqOptions options)
    {
        options.Host = string.IsNullOrWhiteSpace(options.Host) ? "localhost" : options.Host;
        options.Port = options.Port == 0 ? (ushort)5672 : options.Port;
        options.VirtualHost = string.IsNullOrWhiteSpace(options.VirtualHost) ? "/" : options.VirtualHost;
        options.Username = string.IsNullOrWhiteSpace(options.Username) ? "guest" : options.Username;
        options.Password = string.IsNullOrWhiteSpace(options.Password) ? "guest" : options.Password;
        options.HeartbeatSeconds = options.HeartbeatSeconds == 0 ? (ushort)30 : options.HeartbeatSeconds;
        options.PrefetchCount = options.PrefetchCount == 0 ? (ushort)32 : options.PrefetchCount;
        options.ConcurrentMessageLimit = options.ConcurrentMessageLimit < 0 ? 16 : options.ConcurrentMessageLimit;
        options.RetryCount = options.RetryCount < 0 ? 3 : options.RetryCount;
        options.RetryIntervalSeconds = options.RetryIntervalSeconds <= 0 ? 2 : options.RetryIntervalSeconds;

        if (options.ClusterNodes is { Count: > 0 })
        {
            var validNodes = new List<string>(options.ClusterNodes.Count);
            foreach (var node in options.ClusterNodes)
            {
                if (!string.IsNullOrWhiteSpace(node))
                {
                    validNodes.Add(node.Trim());
                }
            }

            options.ClusterNodes = validNodes.Count > 0 ? validNodes : null;
        }
    }
}
