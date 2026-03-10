# SerialPortService 指标与告警 Runbook

## 指标名称（Meter）

`MeterName`: `SerialPortService.GenericHandler`

### 已发布指标

- `serialport.handler.idle_dropped`
- `serialport.handler.overflow_dropped`
- `serialport.handler.unmatched`
- `serialport.handler.retry_count`
- `serialport.handler.timeout_count`
- `serialport.handler.matched_count`
- `serialport.handler.avg_latency_ms`
- `serialport.handler.active_requests`
- `serialport.handler.wait_backlog`
- `serialport.handler.wait_backlog_high_watermark`

标签：`handler`、`port`、`protocol`、`deviceType`

## 库内内置告警（日志级别 Error）

### 1) 超时率告警

触发条件：

- 样本数 `timeouts + matched >= TimeoutRateAlertMinSamples`
- 且 `timeout_rate >= TimeoutRateAlertThresholdPercent`

日志关键字：`Timeout rate alert`

### 2) 队列积压告警

触发条件：

- `wait_backlog >= WaitBacklogAlertThreshold`

日志关键字：`Wait backlog alert`

### 3) 重连失败率告警

触发条件：

- 样本数 `reconnect_total >= ReconnectFailureRateAlertMinSamples`
- 且 `reconnect_exhausted_rate >= ReconnectFailureRateAlertThresholdPercent`

日志关键字：`Reconnect failure-rate alert`

## 生产建议阈值（起步值）

- `TimeoutRateAlertThresholdPercent`: 20
- `TimeoutRateAlertMinSamples`: 20
- `WaitBacklogAlertThreshold`: 1024
- `ReconnectFailureRateAlertThresholdPercent`: 30
- `ReconnectFailureRateAlertMinSamples`: 20

上线后按现场噪声与设备响应特征逐步收敛阈值。

## 告警处置建议

### Timeout rate alert

- 优先检查设备侧响应时间与轮询间隔是否匹配。
- 检查是否出现迟到响应导致请求错配。

### Wait backlog alert

- 检查采集并发是否超出当前 `ResponseChannelCapacity` / `WaitModeQueueCapacity`。
- 评估是否需要增大容量或降低上游采样频率。

### Reconnect failure-rate alert

- 检查物理链路、电源、串口驱动占用。
- 排查是否存在多实例抢占同一串口。
