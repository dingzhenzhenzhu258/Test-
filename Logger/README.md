# Logger

`Logger` 是这个项目里的统一日志基础库，负责：

- 本地业务日志落盘
- OpenTelemetry / OpenObserve 日志上报
- OTLP 断线时自动切到离线补传队列
- 服务器恢复后自动补传，并保留原始日志时间
- 为日志补充调用位置、事件标识、重放标记等诊断信息

## 主要入口

- `Logger/Extensions/LoggerExtensions.cs`
  - `AddSerilogLogging(...)`
  - `StartTrace(...)`
  - `GetReplayQueueMetrics()`
  - `GetReplayQueueDiagnostics()`
- `Logger/Internals/OtlpReplayQueueManager.cs`
  - 离线队列写入、重放、清理、容量控制、`.tmp` 恢复

## 当前行为

### 1. 启动时日志服务器不可用

- 业务日志仍然写入 `logs/app.log`
- Logger 会记录 OTLP 不可用的生命周期日志
- 离线期间的待补传日志进入 `logs/otlp-replay`
- 服务器恢复后自动探测并自动补传
- 补传时使用原始 `TimestampUtc`，不是补传发生时刻

### 2. 启动时日志服务器可用，运行中断开

- 正常日志先走 OTLP
- 一旦 OTLP sink 失败，SelfLog 会触发降级
- 最近一小段时间内的日志会回补进离线队列，减少断点丢失
- 后续新日志继续写本地，并进入补传队列
- 服务器恢复后自动重建 OTLP 转发 sink 并补传

### 3. 多次断开 / 恢复

- 恢复监测器会在每次再次断线后重新启动
- 队列支持多次累积、多次补传
- 已确认上传的事件会通过 `EventId` / `ReplayId` 去重校验，避免整批重复确认

## 本地文件说明

- `logs/app.log`
  - 常规业务日志
- `logs/replay.log`
  - 补传到远端成功后，在本地额外留痕的补传日志
- `logs/fallback.log`
  - Logger 自身的降级、恢复、补传、清理异常提示
  - 不再承载常规业务日志
- `logs/otlp-replay/*.jsonl`
  - 离线补传队列
  - 每行一条待补传日志

## 队列诊断指标

保留旧接口：

```csharp
var metrics = LoggerExtensions.GetReplayQueueMetrics();
```

新增完整诊断接口：

```csharp
var diagnostics = LoggerExtensions.GetReplayQueueDiagnostics();
```

字段说明：

- `FileCount`
  - 当前补传队列文件数
- `TempFileCount`
  - 尚未恢复完成的临时队列文件数
- `TotalBytes`
  - 队列总大小
- `TotalEntries`
  - 队列中仍待处理的总条数
- `PendingConfirmationEntries`
  - 已经发送过但尚未确认成功，仍留在队列中的条数
- `ReplayFailureCount`
  - 累计失败重放次数
- `SuccessfulReplayCount`
  - 当前进程内累计成功确认的补传条数
- `LastSuccessfulReplayUtc`
  - 最近一次成功补传确认时间
- `LastReplayAttemptUtc`
  - 最近一次失败后进入待确认状态的时间

## OpenObserve 配置

当前库支持两种方式设置认证：

### 方式 1. 直接写 Header

```json
"Logger": {
  "Otlp": {
    "Headers": "Authorization=Basic xxxxx"
  }
}
```

### 方式 2. 写用户名密码

```json
"Logger": {
  "Otlp": {
    "Username": "admin@example.com",
    "Password": "Kb123456@"
  }
}
```

如果同时配置了 `Headers` 和 `Username` / `Password`，优先使用 `Headers`。

## 已验证场景

下面这些场景已经实际跑通过：

- 启动时服务离线，稍后恢复，日志自动补传
- 启动时服务在线，运行中断开，再恢复，日志自动补传
- 多次断开 / 多次恢复
- 离线退出后，下次启动触发补传
- 接近进程退出时恢复
- 快速抖动断线
- `.tmp` 队列文件恢复

## 排查建议

- 看不到远端日志时，先检查 `projectName`
- 再检查 `fallback.log` 是否有 OTLP 断开或认证失败提示
- 再确认 `LogsEndpoint`、账号密码、OpenObserve 流和查询时间范围
- 如果本地队列不为空，优先看 `GetReplayQueueDiagnostics()` 里的：
  - `TotalEntries`
  - `PendingConfirmationEntries`
  - `LastSuccessfulReplayUtc`
  - `LastReplayAttemptUtc`
