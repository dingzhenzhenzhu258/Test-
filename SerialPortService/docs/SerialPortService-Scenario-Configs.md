# SerialPortService 场景配置示例

下面给出若干常见场景的 `appsettings` 配置片段（仅展示 `SerialPortService` 部分）。把需要的片段复制到你的 `appsettings.json` 或单独的配置文件并在启动时加载。

注意：配置项位于 `SerialPortService:GenericHandlerOptions`，并可通过 `configuration.GetSection("SerialPortService:GenericHandlerOptions").Get<GenericHandlerOptions>()` 绑定。

场景 A：默认生产（通用启动值）

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 1024,
      "SampleLogInterval": 500,
      "DropWhenNoActiveRequest": true,
      "ResponseChannelFullMode": "Wait",
      "WaitModeQueueCapacity": 4096,
      "WaitBacklogAlertThreshold": 1024,
      "ReconnectIntervalMs": 1000,
      "MaxReconnectAttempts": 3
    },
    "RequestDefaults": { "TimeoutMs": 1000, "RetryCount": 3 }
  }
}
```

说明：适合中等流量的默认部署；兼顾可靠性与内存占用。

场景 B：高吞吐、允许少量丢包（性能优先）

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 4096,
      "SampleLogInterval": 1000,
      "DropWhenNoActiveRequest": false,
      "ResponseChannelFullMode": "DropOldest",
      "WaitModeQueueCapacity": 0,
      "WaitBacklogAlertThreshold": 0,
      "ReconnectIntervalMs": 1000,
      "MaxReconnectAttempts": 3
    },
    "RequestDefaults": { "TimeoutMs": 800, "RetryCount": 2 }
  }
}
```

说明：适合监控类或遥测场景，优先保证解析/消费吞吐，允许丢旧报文以降低延迟与内存。

场景 C：高吞吐、不能丢包（可靠优先）

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 8192,
      "SampleLogInterval": 200,
      "DropWhenNoActiveRequest": false,
      "ResponseChannelFullMode": "Wait",
      "WaitModeQueueCapacity": 32768,
      "WaitBacklogAlertThreshold": 10000,
      "ReconnectIntervalMs": 1000,
      "MaxReconnectAttempts": 5
    },
    "RequestDefaults": { "TimeoutMs": 2000, "RetryCount": 3 }
  }
}
```

说明：适合计量/审计类场景。需要足够内存与后端写入能力（建议配合批量写入与限并发 worker）。

场景 D：低内存/边缘设备（资源受限）

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 256,
      "SampleLogInterval": 1000,
      "DropWhenNoActiveRequest": true,
      "ResponseChannelFullMode": "DropOldest",
      "WaitModeQueueCapacity": 512,
      "WaitBacklogAlertThreshold": 512,
      "ReconnectIntervalMs": 2000,
      "MaxReconnectAttempts": 2
    },
    "RequestDefaults": { "TimeoutMs": 1500, "RetryCount": 1 }
  }
}
```

说明：在内存和 CPU 受限的设备上使用，优先避免内存爆涨和长时间阻塞。

场景 E：测试/本地回归（便于快速反馈）

```json
{
  "SerialPortService": {
    "GenericHandlerOptions": {
      "ResponseChannelCapacity": 128,
      "SampleLogInterval": 1,
      "DropWhenNoActiveRequest": false,
      "ResponseChannelFullMode": "DropOldest",
      "WaitModeQueueCapacity": 256,
      "WaitBacklogAlertThreshold": 0,
      "ReconnectIntervalMs": 500,
      "MaxReconnectAttempts": 1
    },
    "RequestDefaults": { "TimeoutMs": 500, "RetryCount": 0 }
  }
}
```

说明：便于开发调试，开启高频采样日志和较小容量以尽快触发问题再定位。

使用建议：
- 先在短压（5-10 分钟）环境验证配置是否满足峰值；再做 24 小时长压观察内存/GC/线程/等待积压等指标。 
- 若选择 `Wait` 模式，务必监控 `wait_backlog` 并把 `WaitBacklogAlertThreshold` 设置为告警阈值。
- 高可靠场景强烈建议把后端写入改为“有界队列 + 固定 worker + 批量写入”。
