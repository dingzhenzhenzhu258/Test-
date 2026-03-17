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

## 按业务模式关注指标

### 1) 主动请求 / 一问一答

优先关注：

- `serialport.handler.timeout_count`
- `serialport.handler.retry_count`
- `serialport.handler.avg_latency_ms`
- `serialport.handler.active_requests`

说明：这类场景最关键的是“请求能否在预期时间内拿到匹配响应”。

### 2) 被动持续采集 / 高频上报

优先关注：

- `serialport.handler.wait_backlog`
- `serialport.handler.wait_backlog_high_watermark`
- `serialport.handler.overflow_dropped`
- `serialport.handler.unmatched`

说明：这类场景最关键的是消费者是否跟得上、是否开始积压、是否因满队列产生丢弃。

### 3) 多串口并发 / 复杂现场

优先关注：

- `serialport.handler.timeout_count`
- `serialport.handler.wait_backlog`
- 重连失败率告警日志

说明：多串口环境更容易出现端口争用、现场噪声和局部设备异常，需要结合 `port` 标签分别看。 

## 最小看板建议

### 主动请求 / 一问一答

建议至少放这 4 个图：

- `serialport.handler.timeout_count`
- `serialport.handler.retry_count`
- `serialport.handler.avg_latency_ms`
- `serialport.handler.active_requests`

目标：快速判断“请求是否越来越慢、是否已经开始超时、是否存在请求堆积”。

### 被动持续采集 / 高频上报

建议至少放这 4 个图：

- `serialport.handler.wait_backlog`
- `serialport.handler.wait_backlog_high_watermark`
- `serialport.handler.overflow_dropped`
- `serialport.handler.unmatched`

目标：快速判断“消费者是否跟不上、是否已经丢包、是否存在异常帧或错配帧”。

### 多串口并发

建议每个图至少按 `port` 维度拆分：

- `serialport.handler.timeout_count`
- `serialport.handler.wait_backlog`
- `serialport.handler.avg_latency_ms`

目标：避免多个串口聚合后掩盖单个故障端口。

## 症状 → 可能原因 → 处理动作

| 症状 | 可能原因 | 优先处理动作 |
| --- | --- | --- |
| `timeout_count` 持续上涨 | 设备响应变慢、轮询过快、线路抖动、迟到响应错配 | 先拉大超时，再降低轮询频率，检查设备日志和串口线缆 |
| `retry_count` 上涨但 `matched_count` 仍正常 | 偶发抖动、设备忙、现场干扰 | 检查是否只是瞬时抖动；若长期存在，增加退避或优化设备处理节奏 |
| `avg_latency_ms` 持续升高 | 设备处理变慢、消费者阻塞、线程池抖动 | 先定位是设备侧慢还是本地消费慢，再考虑减并发或拆 worker |
| `wait_backlog` 持续升高 | 消费者跟不上、后端持久化太慢、`Wait` 模式积压 | 优先排查消费端；必要时增大容量、改批量写入、增加固定 worker |
| `overflow_dropped` 开始增加 | 通道满、消费者长期跟不上、`DropOldest` 模式开始生效 | 先确认是否允许丢包；不允许则切 `Wait` 并扩容消费者吞吐 |
| `unmatched` 增加 | 迟到响应、协议错配、设备主动上报混入请求响应链路 | 排查协议选择是否正确，检查是否把被动上报错误地当成请求响应处理 |
| 重连失败率告警出现 | 物理链路异常、端口被占用、驱动不稳定 | 检查线缆、电源、串口占用和驱动；按 `port` 维度缩小故障范围 |

## 排障顺序建议

1. 先看 **是否超时**：`timeout_count`、`retry_count`。
2. 再看 **是否积压**：`wait_backlog`、`wait_backlog_high_watermark`。
3. 再看 **是否丢弃或错配**：`overflow_dropped`、`unmatched`。
4. 最后看 **是否是链路故障**：重连失败率告警、端口占用、物理线缆、电源。

这个顺序适合大多数现场，因为“超时”和“积压”通常最先反映业务可见故障。

## 标签使用建议

默认标签：`handler`、`port`、`protocol`、`deviceType`

建议排查顺序：

1. 先按 `port` 拆分，定位是否是单口故障。
2. 再按 `handler` / `deviceType` 拆分，确认是否是某一类设备集中异常。
3. 最后按 `protocol` 观察，判断是否与特定协议路径有关。

说明：

- `port` 维度最重要，适合定位“哪一个串口坏了”。
- `deviceType` 维度适合判断“是不是某一类设备配置或现场环境有共性问题”。
- `protocol` 维度适合判断“是否是协议实现、匹配规则或特定解析器路径上的问题”。

## 告警分级建议

### P1 / 立即处理

满足以下任一情况建议直接按高优先级处理：

- 重连失败率告警持续出现，且端口无法恢复。
- `wait_backlog` 持续升高并伴随 `overflow_dropped` 增长。
- 多个关键串口同时出现 `timeout_count` 快速增长。

影响：通常意味着业务已经出现明显中断、丢包或设备不可用。

### P2 / 尽快处理

满足以下情况建议尽快排查：

- `timeout_count`、`retry_count` 持续升高，但尚未完全不可用。
- `avg_latency_ms` 持续升高，业务响应明显变慢。
- `unmatched` 长时间偏高，但尚未引发大面积失败。

影响：通常表示系统已经进入不稳定状态，继续运行可能演变成 P1。

### P3 / 观察与优化

满足以下情况建议纳入观察：

- 偶发 `retry_count` 波动。
- 小幅 `wait_backlog` 波动但能自行回落。
- 单个现场、单台设备偶发超时且没有持续趋势。

影响：通常不会立即影响业务，但适合作为后续调优和阈值收敛依据。

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
