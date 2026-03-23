# SerialPortService Virtual COM Smoke

适用环境：
- 当前机器存在成对虚拟串口 `COM9` / `COM10`
- 目标是快速验证 `OpenPort`、`TryWrite`、被动解析消费和关闭流程

## 建议验证路径

### 1. 单向被动接收验证

让 `COM10` 作为发送端，用任意串口工具向 `COM10` 写入一行文本，例如：

```text
HELLO-123\r\n
```

应用侧使用库打开 `COM9` 并按扫码枪文本流消费：

```csharp
using System.IO.Ports;
using Microsoft.Extensions.DependencyInjection;
using SerialPortService.Extensions;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;

var services = new ServiceCollection();
services.AddLogging();
services.AddSerialPortService();
using var provider = services.BuildServiceProvider();

var serial = provider.GetRequiredService<ISerialPortService>();
var open = await serial.OpenPortAsync(
    "COM9",
    9600,
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
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach (var text in barcode.ReadParsedPacketsAsync(cts.Token))
    {
        Console.WriteLine($"RX={text}");
        break;
    }
}

await serial.CloseAllAsync();
```

预期：
- `COM9` 成功打开
- 收到一条 `HELLO-123`
- `CloseAllAsync()` 正常返回

### 2. 原始发送验证

如果只验证发送链路，可以把 `COM9` 作为库侧发送端，`COM10` 用串口调试工具观察：

```csharp
var result = await serial.OpenPortAsync(
    "COM9",
    9600,
    Parity.None,
    8,
    StopBits.One,
    HandleEnum.Controller,
    ProtocolEnum.Default);

var write = await serial.TryWrite("COM9", System.Text.Encoding.ASCII.GetBytes("PING\r\n"));
Console.WriteLine($"WriteSuccess={write.IsSuccess}, Message={write.Message}");

await serial.ClosePortAsync("COM9");
```

预期：
- `TryWrite` 返回成功
- `COM10` 端能看到 `PING`

## 建议观察点

- 打开失败时先确认虚拟串口工具没有独占端口
- 被动接收场景优先用 `ReadParsedPacketsAsync`
- 主动发送但不关心匹配响应时优先用 `TryWrite`
- 验证完成后统一走 `ClosePortAsync` 或 `CloseAllAsync`

## 当前库内最适合做虚拟串口冒烟的内置类型

- `HandleEnum.BarcodeScanner`
  适合文本流 `CR/LF` 结尾验证
- `HandleEnum.Controller`
  适合只验证发送链路
- `HandleEnum.CustomProtocol`
  适合后续扩展成双向帧协议联调
