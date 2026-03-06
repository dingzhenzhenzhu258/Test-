# InfraExtensions

`InfraExtensions` 是一个面向 `.NET 8` 的基础设施封装类库，统一提供：

- Redis 默认连接与常用缓存操作封装
- MassTransit + RabbitMQ 默认配置与常用消息操作封装
- 默认配置模板自动初始化（类似 `Logger` 的配置初始化模式）

## 功能概览

## 1) 一站式注册

```csharp
builder.Services.AddInfraDefaults(
    builder.Configuration,
    register: x =>
    {
        x.AddConsumer<MyConsumer>();
    },
    configureEndpoints: (context, cfg) =>
    {
        cfg.ReceiveEndpoint("my-queue", e =>
        {
            var options = context.GetRequiredService<InfraExtensions.Options.RabbitMqOptions>();
            e.ConfigureDefaultConsumerEndpoint<MyConsumer>(context, options);
        });
    },
    // 可选：按项目前缀隔离配置，优先读取 "ApiA:Redis" / "ApiA:RabbitMq"
    sectionPrefix: "ApiA");
```

可选开关（支持参数或配置）：
- `Infra:EnableRedis`：是否启用 Redis（默认 `true`）
- `Infra:EnableMessageBus`：是否启用 MQ（默认 `true`）

选项容错：
- Redis/MQ 的关键参数会做最小化归一化（如空 `Host`、`VirtualHost`、非法超时值等），降低多项目配置差异导致的启动失败风险。

多项目隔离增强：
- Redis 新增 `KeyPrefix`，用于隔离不同项目的 key 空间。
- RabbitMQ 新增 `ClusterNodes`，支持集群节点配置。

例如某个项目只用 Redis、不用 MQ：

```json
{
  "Infra": {
    "EnableRedis": true,
    "EnableMessageBus": false
  }
}
```

等价于：
- `AddRedisDefaults(...)`
- `AddRedisCommonServices()`
- `AddMassTransitDefaults(...)`
- `AddMessageBusCommonServices()`

---

## 2) 默认配置文件自动初始化

```csharp
InfraConfigurationExtensions.EnsureConfigInitialized(
    builder.Environment.ContentRootPath,
    "appsettings.infra.json");

builder.Configuration.AddJsonFile("appsettings.infra.json", optional: true, reloadOnChange: true);
```

> 当目标文件不存在时，会从类库嵌入资源自动释放默认模板。

---

## 3) Redis 常用方法封装

注入接口：`InfraExtensions.Caching.IRedisCacheService`

支持常用能力：
- `GetAsync<T>` / `SetAsync<T>`
- `GetOrSetAsync<T>`
- `ExistsAsync` / `ExpireAsync`
- `DeleteAsync` / `DeleteByPatternAsync`
- `IncrementAsync` / `DecrementAsync`
- Hash 与 Set 常用操作

---

## 4) MQ 常用方法封装

注入接口：`InfraExtensions.Messaging.IMessageBusService`

支持常用能力：
- `PublishAsync<T>`
- `SendAsync<T>`
- `RequestAsync<TRequest, TResponse>`

---

## 配置示例（appsettings.infra.json）

```json
{
  "Redis": {
    "ConnectionString": "localhost:6379,abortConnect=false",
    "KeyPrefix": "smi:",
    "AbortOnConnectFail": false,
    "ConnectTimeout": 5000,
    "SyncTimeout": 5000,
    "AsyncTimeout": 5000,
    "ConnectRetry": 3,
    "KeepAlive": 60,
    "DefaultDatabase": 0,
    "Ssl": false,
    "AllowAdmin": false
  },
  "RabbitMq": {
    "Host": "localhost",
    "ClusterNodes": ["rabbit-node-1", "rabbit-node-2"],
    "Port": 5672,
    "VirtualHost": "/",
    "Username": "guest",
    "Password": "guest",
    "UseSsl": false,
    "SslServerName": "",
    "HeartbeatSeconds": 30,
    "PrefetchCount": 32,
    "ConcurrentMessageLimit": 16,
    "UseInMemoryOutbox": true,
    "UseMessageRetry": true,
    "RetryCount": 3,
    "RetryIntervalSeconds": 2
  }
}
```

多 WebAPI 共享同一个配置文件时，也可以使用前缀隔离：

```json
{
  "ApiA": {
    "Redis": { "ConnectionString": "10.0.0.11:6379" },
    "RabbitMq": { "Host": "10.0.0.21", "Username": "apiA" }
  },
  "ApiB": {
    "Redis": { "ConnectionString": "10.0.1.11:6379" },
    "RabbitMq": { "Host": "10.0.1.21", "Username": "apiB" }
  }
}
```

调用 `AddInfraDefaults(..., sectionPrefix: "ApiA")` 时会优先读取 `ApiA:Redis`、`ApiA:RabbitMq`；若前缀节点不存在，会自动回退到根节点 `Redis`、`RabbitMq`，兼容旧配置。

---

## 项目结构

- `Extensions/ServiceCollectionExtensions.cs`：DI 注册入口
- `Extensions/InfraConfigurationExtensions.cs`：默认配置初始化
- `Caching/IRedisCacheService.cs`：缓存抽象
- `Caching/RedisCacheService.cs`：缓存实现
- `Messaging/IMessageBusService.cs`：消息抽象
- `Messaging/MessageBusService.cs`：消息实现
- `Options/RedisOptions.cs` / `Options/RabbitMqOptions.cs`：配置模型

