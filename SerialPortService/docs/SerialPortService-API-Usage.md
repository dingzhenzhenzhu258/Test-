# SerialPortService API Usage

## 打开和关闭

- 推荐：`OpenPortAsync(...)`
- 推荐：`OpenPortAsync<T>(...)`
- 推荐：`OpenPortAsync<T>(..., Func<IStreamParser<T>> parserFactory)`
- 推荐：`ClosePortAsync(portName)`
- 推荐：`CloseAllAsync()`
- 运维：`RestartPortAsync(portName)`
- 运维：`RestartPortsAsync(portNames)`
- 可用但不推荐作为新代码主路径：`OpenPort(...)`、`OpenPort<T>(...)`、`CloseAll()`

同步版本仍存在，但定位是同步封装入口，不再作为“兼容模型”文档主线。

## 写入语义

- `Write(portName, data)`：异常语义，失败直接抛异常
- `TryWrite(portName, data)`：结果语义，失败返回 `OperateResult<byte[]>`

生产侧优先用 `TryWrite(...)`。

## 接口选择

| 场景 | 推荐接口 |
| --- | --- |
| 主动控制，一问一答 | `SendRequestAsync(...)` |
| 被动持续采集 | `ReadParsedPacketsAsync(...)` |
| 只下发命令 | `TryWrite(...)` |
| UI 轻量通知 | `OnHandleChanged` |

## 扩展模型

### 1. 扩展设备上下文

```csharp
var registrationResult = serial.RegisterContextRegistration(
    "040_my_device",
    new MyDeviceRegistration());
```

规则：

- key 建议使用数字前缀控制优先级
- 数字越小优先级越高
- 重复 key 会返回失败结果，不会覆盖已有注册

### 2. 扩展协议解析器

```csharp
var parserResult = serial.RegisterParser(
    ProtocolEnum.ModbusASCII,
    "050_ascii_parser",
    static () => new MyAsciiParser());
```

规则：

- 注册维度是 `ProtocolEnum + TResult`
- 同一维度重复注册会返回失败结果
- 创建解析器时按实例级注册表解析，不依赖全局静态工厂

### 3. 使用自定义解析器直接开口

```csharp
var open = await serial.OpenPortAsync(
    "COM5",
    115200,
    Parity.None,
    8,
    StopBits.One,
    new MyFrameParser());
```

如果希望 `RestartPortAsync(...)` 后仍能完整重建，优先使用 parser factory：
```csharp
var open = await serial.OpenPortAsync(
    "COM5",
    115200,
    Parity.None,
    8,
    StopBits.One,
    static () => new MyFrameParser());
```

## 运维与诊断

### 服务级健康快照

```csharp
var health = serial.GetHealthSnapshot();
Console.WriteLine($"open={health.OpenPortCount}, running={health.RunningPortCount}, faulted={health.FaultedPortCount}");
```

### 服务级诊断报告

```csharp
var report = serial.GetDiagnosticReport();
Console.WriteLine(report.HealthStatus);
Console.WriteLine(report.RecentErrors.Count);
```

### 单口运行时快照

```csharp
var runtime = serial.GetPortRuntimeSnapshot("COM9");
if (runtime.IsSuccess && runtime.Snapshot is { } snapshot)
{
    Console.WriteLine(snapshot.CloseState);
    Console.WriteLine(snapshot.LastReconnectReason);
    Console.WriteLine(snapshot.RecentErrors.Count);
}
```

### 端口重启

```csharp
var restart = await serial.RestartPortAsync("COM9");
Console.WriteLine(restart.Message);
```

### 批量端口重启

```csharp
var batch = await serial.RestartPortsAsync(new[] { "COM9", "COM10" });
Console.WriteLine($"success={batch.SuccessCount}, failure={batch.FailureCount}");
```

## 被动持续采集示例

```csharp
if (serial.TryGetContext("COM9", out var ctx) && ctx is BarcodeScannerHandler barcode)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    await foreach (var text in barcode.ReadParsedPacketsAsync(cts.Token))
    {
        Console.WriteLine(text);
    }
}
```

## 高速采集建议

- 用 `ReadParsedPacketsAsync(...)` 消费持续数据，不要每条报文都 `Task.Run`
- 持久化侧使用有界队列 + 固定 worker
- 打开 `WaitBacklogAlertThreshold`，不要让积压静默增长
- 长跑时关注运行快照、超时率、重连失败率、事件丢弃数

## 配置

只使用：

- `SerialPortService:GenericHandlerOptions`
- `SerialPortService:RequestDefaults`
