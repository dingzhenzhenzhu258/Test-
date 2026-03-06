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
