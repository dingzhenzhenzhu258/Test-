# SerialPortService API 与使用说明

## 写入接口语义

- `Write(string portName, byte[] data)`
  - 语义：异常流。
  - 端口未打开、发送失败、参数非法时会抛异常。

- `TryWrite(string portName, byte[] data)`
  - 语义：结果流。
  - 返回 `OperateResult<byte[]>`，`IsSuccess=false` 时由业务方处理失败分支。

## 推荐调用方式

生产环境推荐：

1. 常规业务调用使用 `TryWrite`。
2. 对 `IsSuccess=false` 执行统一策略（限次重试、降级、告警、审计）。
3. 仅在需要中断流程时使用 `Write` 并捕获异常。

## 场景：主动式派发 vs 被动高频接收

根据团队的 `copilot-instructions` 规范，在此明确在使用 `GenericHandler` 或特定协议（如 `ModbusHandler`）时，针对不同通信应用场景的接口选择：

| 场景 | 数据方向 | 推荐接口 | 不推荐接口 | 原因 |
| --- | --- | --- | --- | --- |
| 主动控制端 / 一问一答 | Push Request → Matched Response | `SendRequestAsync(...)` | `ReadParsedPacketsAsync(...)` | 需要超时、重试、匹配语义时，应直接复用 Handler 的请求响应能力。 |
| 被动高频收集 / 主动上报 | Push Device Data → Pull by Stream | `ReadParsedPacketsAsync(...)` | `SendRequestAsync(...)` | 数据本身不是某次请求的响应，流式消费更自然且不会强耦合请求生命周期。 |
| 仅发送命令，不等待匹配响应 | Push Only | `TryWrite(...)` | `Write(...)`（生产默认） | 生产建议优先结果语义，失败时走统一重试、退避和告警。 |
| UI / 轻量通知 | Push Callback | `OnHandleChanged` | 在回调中直接长耗时处理 | 回调适合通知，不适合做重 IO、持久化或复杂聚合。 |

- **主动式控制端 (主动派发 / Push via Request)**  
  建议使用带有明确超时重试功能的同步等待机制。  
  **推荐 API:** `SendRequestAsync(byte[] command, int timeout = 1000, int retryCount = 3)`  
  **适用场景:** 作为上位机控制下位机，下发开启激光、回原点、查询特定寄存器值，必须收到匹配该功能码和节点号的准确回应。

- **被动高频收集 (流式推流 / Pull via Stream)**  
  建议采用不会阻塞底层通道的流式读取机制。  
  **推荐 API:** `ReadParsedPacketsAsync(CancellationToken cancellationToken = default)` （返回 `IAsyncEnumerable<T>`）  
  **适用场景:** 设备主动上报状态数据（如连续高配比温度监测流或持续的测距传感器流）。业务层只需通过 `await foreach(var packet in handler.ReadParsedPacketsAsync(...))` 进行不停接收和归档即可，脱离请求/响应强绑定生命周期。  
  *附注：当前版本的 `ModbusPacket` 已是普通对象模型，不需要也不支持 `Rent/Return`。被动推流场景只需关注消费者吞吐和后续持久化策略。*

## 典型失败分支

- 串口未打开：`Message` 包含“未打开”。
- 底层发送失败：`Message` 包含“发送失败”。
- 入参非法：`Message` 包含“不能为空”等提示。

## OpenPort 约束说明

- 同一个串口名重复 `OpenPort` 时，若关键参数不一致（波特率、校验位、数据位、停止位、协议/解析器），库会返回失败。
- 建议流程：`ClosePort` 后再按新参数重新 `OpenPort`。

## 推荐异常处理模板

1. 优先走 `TryWrite`。
2. 失败时记录业务日志 + 设备标识 + 指令摘要。
3. 按业务策略执行限次重试和人工告警。

## 解析报文消费示例

下面示例展示三种常见的解析后消费方式：

1) 异步流消费（推荐用于高吞吐/可取消场景）

```csharp
// 生产建议：有界队列 + 固定 worker，避免每条报文都 Task.Run
var cts = new CancellationTokenSource();
var persistQueue = Channel.CreateBounded<ModbusPacket>(new BoundedChannelOptions(8192)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleWriter = true,
    SingleReader = false
});

var workers = Enumerable.Range(0, 4)
    .Select(_ => Task.Run(async () =>
    {
        await foreach (var pkt in persistQueue.Reader.ReadAllAsync(cts.Token))
        {
            await PersistPacketAsync(pkt);
        }
    }, cts.Token))
    .ToArray();

try
{
    await foreach (var pkt in modbusHandler.ReadParsedPacketsAsync(cts.Token))
    {
        await persistQueue.Writer.WriteAsync(pkt, cts.Token);
    }
}
catch (OperationCanceledException) { }
finally
{
    persistQueue.Writer.TryComplete();
    await Task.WhenAll(workers);
}
```

2) 事件订阅（轻量，同步通知）

```csharp
modbusContext.OnHandleChanged += (sender, e) =>
{
    var result = (OperateResult<ModbusPacket>)e;
    // UI 线程需调度
    Application.Current.Dispatcher.Invoke(() => ShowOnUi(result.Content));
};
```

3) 在 Handler 内部高性能处理（覆写 Hook）

```csharp
// 在自定义 Handler 中覆写
protected override void OnParsed(ModbusPacket pkt)
{
    base.OnParsed(pkt); // 保留基类分发与统计
    // 直接内存写入或聚合（注意不要阻塞）
    _localAggregator.Add(pkt);
}
```

要点：
- 若使用 `ReadParsedPacketsAsync`，请传入 `CancellationToken` 并确保消费者能跟上生产速率；否则可能触发通道积压或丢包，具体行为取决于 `ResponseChannelFullMode` 配置。
- `OnHandleChanged` 回调可能在解析线程触发；若在 UI 使用需切换线程上下文。
- 不建议对每条报文执行 `_ = Task.Run(...)`；高吞吐长跑下容易造成任务堆积、线程池抖动和后续超时。

### 适用场景与接口选择指南

针对不同类型的硬件设备与业务需求，推荐遵循以下原则选择正确的收发接口：

#### 1. 持续被动采集（未经请求数据主动上报）
* **代表设备**：扫码枪、连续称重电子秤、心跳业务、定频RFID扫描器。
* **适用接口**：**强烈推荐**使用异步流 `ReadParsedPacketsAsync`。
* **原因**：设备随时产生无法预测体量的突发数据。使用 `await foreach` 配合后台长任务，通过底层 Channel 缓冲区平滑削峰，完美应对流量突变且无需手动调度线程。

#### 2. 半主动/订阅型连续采集（一次指令，连绵不断）
* **代表设备**：需要上位机发送“开始测试”指令后，才以较高频率持续爆传数据的仪器。
* **适用接口**：**非常适用**异步流 `ReadParsedPacketsAsync`。
* **原因**：通过 `TryWrite` 发送单条指令后，下机挂起一个 `await foreach` 的长期消费工作流接收连续测试结果，直到下发或者接收到“停止测试”信号（借由 `CancellationToken` 取消消费）。

#### 3. 传统主从“一问一答”（被动从站式主动轮询）
* **代表设备**：标准 Modbus 从站（如各类温湿度计、常规PLC寄存器读写），完全遵守“不问不说”的应答纪律。
* **适用接口**：**坚决推荐**使用带匹配语义的 `SendRequestAsync`（如 `IModbusContext.SendRequestAsync`）。**不推荐**用流式接收。
* **原因**：`SendRequestAsync` 内部天然集成了发送、挂起等待、特征字匹配与超时重试。如果在此场景强行用 `ReadParsedPacketsAsync` 等待数据，需要在业务层重新开发异常脆弱的请求序号映射（Request-Id mapping），违背了组件的设计初衷。

## 配置片段（appsettings.json 示例）

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 4096,
      "ResponseChannelFullMode": "Wait",
      "WaitModeQueueCapacity": 8192,
      "DropWhenNoActiveRequest": false,
      "SampleLogInterval": 200,
      "ReconnectIntervalMs": 1000,
      "MaxReconnectAttempts": 5,
      "WaitBacklogAlertThreshold": 2048
    }
  }
}
```

示例：在 .NET 启动代码中绑定配置并注入：

```csharp
var options = configuration.GetSection("SerialPortService:GenericHandlerOptions").Get<GenericHandlerOptions>();
var handler = new ModbusHandler("COM3", 9600, Parity.None, 8, StopBits.One, logger, options);
```

如果是**主动请求 / 一问一答**场景，优先使用 `SendRequestAsync(...)`；
如果是**被动持续采集 / 高频上报**场景，优先使用 `ReadParsedPacketsAsync(...)`；
如果只是**发送命令且不希望抛异常**，优先使用 `TryWrite(...)`。
