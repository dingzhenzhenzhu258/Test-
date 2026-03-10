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
- 上线后优先关注：`timeout_rate`、`overflow_dropped_rate`。

---

## 写入错误语义（生产必读）

- `Write(portName, data)`：异常语义。端口未打开或发送失败会抛异常。
- `TryWrite(portName, data)`：结果语义。失败时返回 `OperateResult<byte[]>`，不抛异常。

生产场景建议优先使用 `TryWrite`，并将失败分支接入统一重试与告警。

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
    Application.Current.Dispatcher.Invoke(() => ShowOnUi(result.Result));
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
