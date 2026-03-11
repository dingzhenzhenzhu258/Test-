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
    Application.Current.Dispatcher.Invoke(() => ShowOnUi(result.Result));
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
- 若使用 `ReadParsedPacketsAsync`，请传入 `CancellationToken` 并确保消费者能跟上生产速率；否则可能触发通道丢包（默认是 DropOldest）。
- `OnHandleChanged` 回调可能在解析线程触发；若在 UI 使用需切换线程上下文。
- 不建议对每条报文执行 `_ = Task.Run(...)`；高吞吐长跑下容易造成任务堆积、线程池抖动和后续超时。

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
