# SerialPortService

`SerialPortService` 是面向高频串口采集和协议扩展的核心类库，当前版本只保留新的实例级扩展模型。

## 当前约束

- 设备上下文扩展走 `RegisterContextRegistration(...)`
- 协议解析器扩展走 `RegisterParser<T>(...)`
- 需要可重启的自定义协议端口优先走 `OpenPortAsync<T>(..., Func<IStreamParser<T>> parserFactory)`
- 配置节只认 `SerialPortService:GenericHandlerOptions`
- 不再保留旧的全局注册语义
- 不再保留旧配置节 `SerialPortService:GenericHandler`

## 核心能力

- 高速采集管线：异步 IO、发送队列、原始字节缓冲、解析事件通道
- 运行时诊断：超时率、积压告警、重连失败率、运行快照、最近事件/错误窗口
- 协议扩展：内置 Modbus RTU、自定义协议，支持实例级解析器注册
- 设备扩展：内置扫码枪、控制器、温度传感器、伺服、自定义协议处理器
- 请求模型：支持请求-响应和被动持续采集两类模式
- 运维接口：服务级健康快照、服务级诊断报告、单口运行时快照、单口/批量端口重启

## 快速使用

```csharp
var options = new GenericHandlerOptions
{
    ResponseChannelCapacity = 4096,
    WaitModeQueueCapacity = 8192,
    SendChannelCapacity = 2048,
    RawInputChannelCapacity = 2048,
    DispatchParsedEventAsync = true,
    WaitBacklogAlertThreshold = 2048
};

var service = new SerialPortServiceBase(loggerFactory, options);

var open = await service.OpenPortAsync(
    "COM3",
    9600,
    Parity.None,
    8,
    StopBits.One,
    HandleEnum.Default,
    ProtocolEnum.ModbusRTU);
```

## 扩展示例

### 注册新的设备上下文

```csharp
var result = service.RegisterContextRegistration(
    "040_my_device",
    new MyDeviceRegistration());

if (!result.IsSuccess)
{
    Console.WriteLine(result.Message);
}
```

### 注册新的协议解析器

```csharp
var parserResult = service.RegisterParser(
    ProtocolEnum.ModbusASCII,
    "050_ascii_text",
    static () => new MyAsciiParser());
```

### 查看服务健康状态

```csharp
var health = service.GetHealthSnapshot();

foreach (var port in health.Ports)
{
    Console.WriteLine($"{port.PortName} running={port.IsRunning} closeState={port.CloseState}");
}
```

### 查看单口快照并执行重启

```csharp
var snapshot = service.GetPortRuntimeSnapshot("COM3");
var restart = await service.RestartPortAsync("COM3");
var batch = await service.RestartPortsAsync(new[] { "COM3", "COM4" });
```

### 使用 parser factory 打开可重启的自定义协议端口
```csharp
var open = await service.OpenPortAsync(
    "COM5",
    115200,
    Parity.None,
    8,
    StopBits.One,
    static () => new MyFrameParser());
```

### 被动持续采集

```csharp
if (service.TryGetContext("COM3", out var ctx) && ctx is ModbusHandler modbus)
{
    using var cts = new CancellationTokenSource();

    await foreach (var packet in modbus.ReadParsedPacketsAsync(cts.Token))
    {
        await PersistAsync(packet);
    }
}
```

## 推荐接口选择

- 一问一答控制场景：优先 `SendRequestAsync(...)`
- 被动持续上报场景：优先 `ReadParsedPacketsAsync(...)`
- 只发送不关心匹配响应：优先 `TryWrite(...)`
- 新代码优先 `OpenPortAsync(...)` / `CloseAllAsync()`

## 配置

只使用下面这个配置节：

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 4096,
      "WaitModeQueueCapacity": 8192,
      "SendChannelCapacity": 2048,
      "RawInputChannelCapacity": 2048,
      "RawReadBufferSize": 8192,
      "SerialPortReadBufferSize": 1048576,
      "EnableRawReadChunkLog": false,
      "RawBytesLogIntervalSeconds": 60,
      "DispatchParsedEventAsync": true,
      "ParsedEventChannelCapacity": 2048,
      "ParsedEventChannelFullMode": "DropOldest",
      "WaitBacklogAlertThreshold": 2048
    },
    "RequestDefaults": {
      "TimeoutMs": 1000,
      "RetryCount": 3
    }
  }
}
```

## 文档

- `docs/SerialPortService-API-Usage.md`
- `docs/SerialPortService-Scenario-Configs.md`
- `docs/SerialPortService-VirtualCom-Smoke.md`
- `docs/SerialPortService-VirtualCom-LongRun.md`
- `docs/SerialPortService-Metrics-Runbook.md`
