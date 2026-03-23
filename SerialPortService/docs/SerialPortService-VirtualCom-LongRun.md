# SerialPortService Virtual COM Long Run

适用环境：

- 当前机器存在虚拟串口对 `COM9` / `COM10`
- 目标是验证长时间高速收发、积压告警、关闭超时保护和内存稳定性

## 建议分工

- `COM9`：库侧打开
- `COM10`：压测发送端

## 压测目标

- 连续运行 1h、4h、8h
- 观察是否出现积压持续上升
- 观察关闭是否在超时保护内完成
- 观察是否出现解析事件丢弃、发送堆积、异常重连

## 建议配置

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 8192,
      "WaitModeQueueCapacity": 32768,
      "SendChannelCapacity": 4096,
      "RawInputChannelCapacity": 8192,
      "RawReadBufferSize": 8192,
      "SerialPortReadBufferSize": 1048576,
      "EnableRawReadChunkLog": false,
      "RawBytesLogIntervalSeconds": 60,
      "DispatchParsedEventAsync": true,
      "ParsedEventChannelCapacity": 4096,
      "ParsedEventChannelFullMode": "DropOldest",
      "WaitBacklogAlertThreshold": 10000,
      "TimeoutRateAlertThresholdPercent": 20,
      "TimeoutRateAlertMinSamples": 20,
      "ReconnectFailureRateAlertThresholdPercent": 30,
      "ReconnectFailureRateAlertMinSamples": 20
    }
  }
}
```

## 接收侧示例

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddSerialPortService(configuration);
using var provider = services.BuildServiceProvider();

var serial = provider.GetRequiredService<ISerialPortService>();
var open = await serial.OpenPortAsync(
    "COM9",
    115200,
    Parity.None,
    8,
    StopBits.One,
    HandleEnum.BarcodeScanner,
    ProtocolEnum.Default);

if (!open.IsSuccess)
{
    Console.WriteLine(open.Message);
    return;
}

if (serial.TryGetContext("COM9", out var ctx) && ctx is BarcodeScannerHandler barcode)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromHours(4));
    var count = 0L;

    await foreach (var text in barcode.ReadParsedPacketsAsync(cts.Token))
    {
        count++;
        if (count % 10000 == 0)
        {
            Console.WriteLine($"RX={count}");
        }
    }
}

await serial.CloseAllAsync();
```

## 发送侧建议

可用任意串口工具向 `COM10` 持续发送固定帧，例如：

```text
LOAD-000001\r\n
LOAD-000002\r\n
LOAD-000003\r\n
```

如果使用脚本工具，建议控制：

- 固定帧长
- 固定发送频率
- 每轮记录总发送量
- 每 5 分钟记录一次发送速率

## 重点观察项

- `wait_backlog` 是否持续上升
- `timeout_count` 是否突增
- `Reconnect failure-rate alert` 是否出现
- 关闭时是否命中 3 秒超时保护
- 进程内存是否持续单向增长

## 判定标准

- 运行期间无未处理异常
- 停止后可以正常关闭端口
- 内存曲线无持续泄漏趋势
- 累积吞吐稳定，没有长时间停摆
