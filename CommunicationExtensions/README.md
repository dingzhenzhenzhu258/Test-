# CommunicationExtensions

一个面向 `.NET 8` 的通信辅助库，提供：

- 基于 `RestSharp` 的 HTTP 调用封装（`RestHttpHelper`）
- 基于 `Grpc.Net.Client` 的 gRPC 调用封装（`GrpcHelper`）
- 按键（Keyed）注册 HTTP 客户端的依赖注入扩展（`HttpRestClientExtensions`）

---

## 1. 目标与设计思路

该库关注“通用调用能力”而不是“业务模型定义”，目标是：

1. 统一请求发送方式，减少重复代码。
2. 对常见调用模式（Unary、Streaming、REST 常见动词）做轻量封装。
3. 保持可扩展：调用方可继续控制请求/响应模型、Header、取消令牌、超时等。

---

## 2. 运行环境

- `TargetFramework`: `net8.0`
- `C#`: `12.0`
- 关键依赖：
  - `RestSharp`
  - `Newtonsoft.Json`
  - `Grpc.Net.Client`

---

## 3. HTTP 能力（RestHttpHelper）

文件：`CommunicationExtensions/Http/RestHttpHelper.cs`

### 3.1 已支持方法

- `GetAsync<T>(...)`
- `PostJsonAsync<TRequest, TResponse>(...)`
- `PutJsonAsync<TRequest, TResponse>(...)`
- `PatchJsonAsync<TRequest, TResponse>(...)`
- `DeleteAsync<TResponse>(...)`

### 3.2 行为说明

1. 请求成功判定：依赖 `RestResponse.IsSuccessful`。
2. 响应为空内容：返回 `default`。
3. 响应非空：使用 `JsonConvert.DeserializeObject<T>(...)` 反序列化。
4. 请求失败：抛出 `InvalidOperationException`，包含状态码与错误信息。

### 3.3 使用示例

```csharp
var client = new RestClient("https://api.example.com");
var helper = new RestHttpHelper(client);

var headers = new Dictionary<string, string>
{
    ["Authorization"] = "Bearer xxx"
};

var detail = await helper.GetAsync<MyDto>("/v1/items/1", headers, cancellationToken);

var created = await helper.PostJsonAsync<CreateRequest, ApiResult>(
    "/v1/items",
    new CreateRequest { Name = "demo" },
    headers,
    cancellationToken);
```

### 3.4 注意事项

- `PostJsonAsync / PutJsonAsync / PatchJsonAsync` 的 `TRequest` 要求 `class`（引用类型）。
- 如需统一序列化设置（命名策略、日期格式），建议在上层做统一约定。
- 对于下载文件、非 JSON 返回体等场景，可在该类基础上扩展。

---

## 4. HTTP DI 扩展（HttpRestClientExtensions）

文件：`CommunicationExtensions/Http/HttpRestClientExtensions.cs`

### 4.1 方法

- `AddHttpRestClient(this IServiceCollection services, string key, string apiUrl)`

该方法会按 `key` 注册一个 `RestHttpHelper`，内部使用 `new RestClient(apiUrl)` 创建客户端。

### 4.2 注册与解析示例

```csharp
// 注册
services.AddHttpRestClient("UserApi", "https://api.example.com");

// 解析（.NET 8 Keyed Service）
var helper = serviceProvider.GetRequiredKeyedService<RestHttpHelper>("UserApi");
```

### 4.3 适用场景

- 多个外部 API 基础地址并存（按 key 区分）。
- 避免手动维护多个命名实例。

---

## 5. gRPC 能力（GrpcHelper）

文件：`CommunicationExtensions/Grpc/GrpcHelper.cs`

### 5.1 通道创建

- `CreateChannel(string address)`
- `CreateChannel(Uri address)`

### 5.2 Unary 调用

- `UnaryCallAsync(..., GrpcChannel channel, ...)`
- `UnaryCallAsync(..., string address, ...)`
- `UnaryCallWithTimeoutAsync(..., string address, ..., TimeSpan timeout, ...)`

### 5.3 Streaming 调用

- 服务端流：`ServerStreamingCallAsync(...)`
- 客户端流：`ClientStreamingCallAsync(...)`
- 双向流：`DuplexStreamingCallAsync(...)`

以上均提供 `channel` 与 `address` 两种重载（便于复用通道或临时调用）。

### 5.4 使用示例（Unary）

```csharp
var response = await GrpcHelper.UnaryCallAsync<MyGrpcClient, MyRequest, MyReply>(
    "https://localhost:5001",
    channel => new MyGrpcClient(channel),
    (client, request, ct) => client.GetDataAsync(request, cancellationToken: ct).ResponseAsync,
    new MyRequest { Id = 1 },
    cancellationToken);
```

### 5.5 使用建议

- 高频调用建议优先复用 `GrpcChannel`。
- 对外部依赖不稳定场景，建议在上层组合重试/熔断策略。
- `UnaryCallWithTimeoutAsync` 适合快速加超时控制。

---

## 6. 错误处理策略建议

### 6.1 HTTP

当前策略是“失败即抛异常”。如果你的业务更偏向“状态对象返回”，可在上层统一捕获并映射到业务响应模型。

### 6.2 gRPC

建议在调用方捕获 `RpcException`，按状态码（如 `DeadlineExceeded`、`Unavailable`）分类处理。

---

## 7. 扩展建议

可按项目需要继续增加：

1. HTTP 超时重载（按请求粒度控制）。
2. HTTP 非 JSON 场景（文件、表单、多部分上传）。
3. gRPC 调用元数据（Metadata）统一注入。
4. 统一日志与链路追踪（请求 ID、耗时、重试次数）。

---

## 8. 快速检查清单

- [ ] 是否传入了有效 `apiUrl` / `address`
- [ ] 是否正确传递了认证 Header / Token
- [ ] 是否传入了 `CancellationToken`
- [ ] 是否对异常进行统一处理与日志记录
- [ ] 是否在高并发下复用了 `GrpcChannel`

---

## 9. 目录概览

```text
CommunicationExtensions/
  ├─ Grpc/
  │   └─ GrpcHelper.cs
  ├─ Http/
  │   ├─ RestHttpHelper.cs
  │   └─ HttpRestClientExtensions.cs
  └─ CommunicationExtensions.csproj
```

---

如果你希望，我可以下一步再补一份“面向业务团队”的示例文档（如在 `SMI.Api/Program.cs` 的完整接入示例、统一异常中间件配合方式、以及测试样例）。
