# SerialPortService Scenario Configs

所有配置统一放在 `SerialPortService:GenericHandlerOptions`。

## 通用稳定型

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 1024,
      "WaitModeQueueCapacity": 4096,
      "SendChannelCapacity": 1024,
      "RawInputChannelCapacity": 1024,
      "ResponseChannelFullMode": "Wait",
      "DispatchParsedEventAsync": true,
      "ParsedEventChannelCapacity": 1024,
      "ParsedEventChannelFullMode": "DropOldest",
      "WaitBacklogAlertThreshold": 1024,
      "ReconnectIntervalMs": 1000,
      "MaxReconnectAttempts": 3
    }
  }
}
```

## 高频吞吐优先

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 4096,
      "WaitModeQueueCapacity": 8192,
      "SendChannelCapacity": 2048,
      "RawInputChannelCapacity": 4096,
      "ResponseChannelFullMode": "DropOldest",
      "DispatchParsedEventAsync": true,
      "ParsedEventChannelCapacity": 2048,
      "ParsedEventChannelFullMode": "DropOldest",
      "WaitBacklogAlertThreshold": 2048
    }
  }
}
```

## 高频可靠优先

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 8192,
      "WaitModeQueueCapacity": 32768,
      "SendChannelCapacity": 4096,
      "RawInputChannelCapacity": 8192,
      "ResponseChannelFullMode": "Wait",
      "DispatchParsedEventAsync": true,
      "ParsedEventChannelCapacity": 4096,
      "ParsedEventChannelFullMode": "Wait",
      "WaitBacklogAlertThreshold": 10000,
      "ReconnectIntervalMs": 1000,
      "MaxReconnectAttempts": 5
    }
  }
}
```

## 低资源边缘设备

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 256,
      "WaitModeQueueCapacity": 512,
      "SendChannelCapacity": 256,
      "RawInputChannelCapacity": 256,
      "ResponseChannelFullMode": "DropOldest",
      "DispatchParsedEventAsync": true,
      "ParsedEventChannelCapacity": 256,
      "ParsedEventChannelFullMode": "DropOldest",
      "WaitBacklogAlertThreshold": 256,
      "ReconnectIntervalMs": 2000,
      "MaxReconnectAttempts": 2
    }
  }
}
```

## 自定义协议

```csharp
var parserResult = serial.RegisterParser(
    ProtocolEnum.ModbusASCII,
    "040_custom_ascii",
    static () => new MyAsciiParser());
```

```csharp
var open = await serial.OpenPortAsync(
    "COM5",
    115200,
    Parity.None,
    8,
    StopBits.One,
    new MyAsciiParser());
```

## 说明

- 不再支持 `SerialPortService:GenericHandler`
- 每个 `SerialPortServiceBase` 实例拥有自己的上下文注册表和解析器注册表
- 重连策略现在随 `GenericHandlerOptions` 一起按实例和端口下发，不再依赖进程级静态状态
