# SerialPortService 使用说明

`SerialPortService` 是统一串口通信库，提供：

- 多协议统一接入（默认 Modbus + 可扩展自定义协议）
- 高并发采集优化（异步 IO + Channel + 限流策略）
- 请求/响应匹配与重试超时
- 指标上报（可接 OpenTelemetry / OpenObserve）
- 断线自动重连（可配置）

---

## 文档入口

- API + 使用文档：`docs/SerialPortService-API-Usage.md`
- 指标看板与告警建议：`docs/SerialPortService-Metrics-Runbook.md`
- 版本与兼容性说明：`docs/SerialPortService-Versioning.md`
- 场景配置示例：`docs/SerialPortService-Scenario-Configs.md`

---

## 项目结构

```
SerialPortService/
├── Extensions/                       # DI 注册扩展（AddSerialPortService）
├── Helpers/                          # 工具方法（CRC 等）
├── Models/
│   ├── Enums/                        # 所有枚举（HandleEnum / ProtocolEnum / AlarmEnums 等）
│   ├── Modbus/                       # Modbus 专属模型（ModbusPacket / ModbusException）
│   ├── CustomFrame.cs                # 自定义协议帧
│   ├── DataPacket.cs
│   ├── OperateResult.cs
│   └── ProtocolExceptions.cs         # 通用协议异常
├── Services/
│   ├── Handler/
│   │   ├── Core/                     # 基础设施（GenericHandler / ParserPortContext / IResponseMatcher / Options）
│   │   ├── Devices/                  # 具体设备处理器（ModbusHandler / AudibleVisualAlarmHandler 等）
│   │   └── Metrics/                  # 指标快照、发布器、提供者接口（全 internal）
│   ├── Interfaces/                   # ISerialPortService / IPortContext / IStreamParser 等
│   ├── Parser/                       # 协议解析器（ModbusRtuParser / BarcodeParser / CustomProtocolParser 等）
│   ├── Protocols/Modbus/Functions/   # Modbus 功能码帧构建
│   ├── PortContext.cs                # 串口上下文基类（生命周期 + Send）
│   ├── PortContext.Pipeline.cs       # IO读取 / 解析 / 发送 / 诊断日志循环（partial）
│   ├── PortContext.Reconnect.cs      # 重连逻辑与失败率告警（partial）
│   ├── PortContextFactory.cs         # 上下文工厂（设备路由 / 协议推断）
│   ├── ParserFactory.cs              # 解析器工厂
│   ├── SerialPortServiceBase.cs      # 对外服务实现（OpenPort / ClosePort / Write / TryWrite）
│   └── SerialPortReconnectPolicy.cs  # 全局重连策略（进程级静态配置）
└── docs/
```

---

## 快速开始

```csharp
var options = new GenericHandlerOptions
{
    ResponseChannelCapacity = 512,
    SampleLogInterval = 200,
    DropWhenNoActiveRequest = true,
    ResponseChannelFullMode = BoundedChannelFullMode.Wait,
    ReconnectIntervalMs = 1000,
    MaxReconnectAttempts = 3
};

var service = new SerialPortServiceBase(loggerFactory, options);
var result = service.OpenPort("COM3", 9600, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusRTU);
```

---

## 关键建议

- 所有项目统一通过你自定义 `Logger` 库写日志。
- 每个应用都从 `appsettings.json` 读取 `GenericHandlerOptions` 后注入。
- 上线后优先关注：`serialport.handler.timeout_count`、`serialport.handler.wait_backlog`，以及 `Reconnect failure-rate alert` 日志。

---

## 写入错误语义（生产必读）

- `Write(portName, data)`：异常语义。端口未打开或发送失败会抛异常。
- `TryWrite(portName, data)`：结果语义。失败时返回 `OperateResult<byte[]>`，不抛异常。

生产场景建议优先使用 `TryWrite`，并将失败分支接入统一重试与告警。

## 场景与接口选择

| 场景 | 通信模式 | 推荐接口 | 说明 |
| --- | --- | --- | --- |
| 主动控制 / 一问一答 | Push Request → Matched Response | `SendRequestAsync(...)` | 适合标准 Modbus 主从轮询、显式读写寄存器、控制类命令。 |
| 被动持续采集 | Push Device Data → Pull by Stream | `ReadParsedPacketsAsync(...)` | 适合设备主动上报、连续测量流、扫码流。业务层使用 `await foreach` 持续消费。 |
| 仅发送、不关心匹配响应 | Push Only | `TryWrite(...)` | 适合告警灯、简单触发命令、业务自行处理失败重试。 |
| UI / 轻量通知 | Push Callback | `OnHandleChanged` | 适合页面刷新、轻量提示；不适合高吞吐重处理。 |

选择原则：

- **需要“请求-响应匹配、超时、重试”**：优先选 `SendRequestAsync`。
- **设备自己持续上报数据**：优先选 `ReadParsedPacketsAsync`。
- **只想发命令，失败不抛异常**：优先选 `TryWrite`。
- **只做简单通知**：可用 `OnHandleChanged`，但不要在回调里做重 IO 或长耗时逻辑。

---

## 高压测试建议流程

1. **短压**（5~10 分钟）
   - 验证发送吞吐与平均响应时延是否稳定。
2. **长压**（24h+）
   - 验证内存、任务数量、重连行为是否持续稳定。
3. **故障注入压测**
   - 断线、抖动、噪声、设备迟到响应、端口占用冲突。

压测期间重点关注：

- `serialport.handler.timeout_count`
- `serialport.handler.wait_backlog`
- `Reconnect failure-rate alert` 日志

---

## 解析报文消费示例

下面示例展示如何在 README 中快速使用 `ReadParsedPacketsAsync`、事件订阅和覆写 `OnParsed` 三种消费方式（详见 `docs/SerialPortService-API-Usage.md`）。

1) 异步流消费（适合高吞吐）

```csharp
// 假设已通过 service.OpenPort(...) 得到 modbusHandler: ModbusHandler
var cts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    try
    {
        await foreach (var pkt in modbusHandler.ReadParsedPacketsAsync(cts.Token))
        {
            // 异步持久化或转发
            _ = Task.Run(() => PersistPacketAsync(pkt));
        }
    }
    catch (OperationCanceledException) { }
});

// 取消示例
// cts.Cancel();
```

2) 事件订阅（适合 UI / 轻量通知）

```csharp
modbusContext.OnHandleChanged += (sender, e) =>
{
    var result = (OperateResult<ModbusPacket>)e;
    Application.Current.Dispatcher.Invoke(() => ShowOnUi(result.Content));
};
```

3) 覆写 `OnParsed`（适合协议内部高性能处理）

```csharp
public class MyHandler : GenericHandler<ModbusPacket>
{
    protected override void OnParsed(ModbusPacket pkt)
    {
        base.OnParsed(pkt); // 保留分发与统计
        // 直接内存聚合或发送到高性能队列，不要阻塞
        _localQueue.Enqueue(pkt);
    }
}
```

更多细节与注意事项请参见 `docs/SerialPortService-API-Usage.md`。
