# Logger 库完整说明

`Logger` 是解决方案内统一日志基础设施，负责：

- 统一日志输出（`Serilog`）
- 统一追踪/指标采集（`OpenTelemetry`）
- 统一故障降级与恢复（OTLP 不可达时自动回退）
- 统一上下文增强（方法名、文件、行号、设备信息）

支持场景：`WebAPI`、`WPF/桌面`、后台服务。

---

## 1. 项目结构

- `Extensions/LoggerExtensions.cs`
  - 日志主入口：`AddSerilogLogging(...)`
  - OTLP 可达性探测、降级、恢复探测、热恢复
  - 自定义追踪入口：`StartTrace(...)`
- `Helpers/LoggerHelper.cs`
  - 统一扩展方法：`ILogger.AddLog(...)`
  - UI 联动事件：`OnUILog`
- `Extensions/LoggerLevelManager.cs`
  - 运行时动态日志级别控制
- `Extensions/GlobalDeviceInfo.cs`
  - 机器名、版本、IP、MAC 采集
- `Extensions/WebAPI/*`
  - `GlobalExceptionMiddleware`：全局异常兜底
  - `RequestResponseLoggingMiddleware`：请求响应审计日志
- `Logger.wpf/Extensions/WpfExceptionExtensions.cs`
  - WPF 全局异常订阅（UI 线程/后台线程/Task）

---

## 2. 快速接入

### 2.1 通用注册（DI）

```csharp
services.AddSerilogLogging(configuration, projectName: "YourServiceName", isWebApi: false);
```

> `projectName` 必须唯一，且不能使用默认值 `xxxapi`。

### 2.2 WebAPI 推荐顺序

```csharp
app.UseGlobalExceptionHandler();
app.UseRequestResponseLogging();
```

### 2.3 WPF 推荐接入

```csharp
app.SubscribeGlobalExceptions(logger);
```

---

## 3. OTLP 智能降级与自动恢复

### 3.1 启动阶段

- 若 `OTLP` 可达：正常启用日志/追踪/指标导出。
- 若 `OTLP` 不可达：
  - 常规业务日志继续写入本地 `logs/app.log`；
  - `logs/fallback.log` 仅记录 `OTLP` 失败/恢复提示与补传异常；
  - 记录明确告警；
  - 启动后台恢复探测。

### 3.2 运行阶段

- `Logs`：可在端点恢复后自动热恢复（无需重启，受配置控制）。
- `Tracing/Metrics`：可保持导出器启用，端点恢复后自动继续上报（受配置控制）。
- `Tracing/Metrics`：也可启用“运行时热重建模式”，在启动时未挂载导出器的情况下，端点恢复后动态重建并恢复上报（受配置控制）。
- `Logs`：支持本地落盘补传队列（JSONL），离线期间写入 `logs/otlp-replay.queue.jsonl`，恢复后按批重放。

### 3.3 关键保障

- SelfLog 一次性异常告警抑制，避免日志风暴。
- 本地兜底日志持续可用。
- 恢复后有明确提示，便于运维确认状态。

---

## 4. 配置项（`Logger/appsettings.json`）

`Logger:Otlp`：

- `Enabled`：总开关（是否启用 OTLP 能力）
- `ProbeOnStartup`：是否启动时探测端点可达性
- `ProbeTimeoutMs`：探测超时（毫秒）
- `AutoRecoverProbe`：是否启用后台恢复探测
- `AutoRecoverProbeIntervalMs`：恢复探测周期（毫秒）
- `AutoRecoverApplyForLogs`：端点恢复后是否自动热恢复日志导出
- `AutoRecoverApplyForTelemetry`：是否让追踪/指标保持自动恢复策略
- `AutoRecoverApplyForTelemetryRuntimeSwitch`：是否启用 Tracing/Metrics 运行时热重建（默认 `false`）
- `ReplayQueueEnabled`：是否启用离线日志补传队列
- `ReplayBatchSize`：每次恢复时最大重放条数
- `ReplayQueueMaxFileSizeMb`：单个补传分段文件上限（MB）
- `ReplayQueueMaxTotalSizeMb`：补传目录总大小上限（MB）
- `ReplayQueueMaxAgeHours`：补传文件最大保留时长（小时）
- `ReplayQueueOverflowStrategy`：容量超限策略（`DropOldest` / `RejectNew` / `WarnOnly`）
- `LogsEndpoint` / `TracesEndpoint` / `MetricsEndpoint`：三类导出端点

`Serilog`：

- `MinimumLevel`：默认及覆盖级别
- `WriteTo`：控制台/常规文件输出模板（默认业务日志写入 `logs/app.log`）
- `Enrich`：附加上下文（线程、LogContext 等）

---

## 5. 公开能力清单

### 5.1 `LoggerExtensions`

- `EnsureConfigInitialized(...)`：自动释放默认配置文件
- `AddSerilogLogging(...)`：注册日志/追踪/指标全链路
- `StartTrace(...)`：创建自定义耗时追踪片段

### 5.2 `LoggerHelper`

- `AddLog(...)`：统一日志扩展（支持异常、UI、调用位置信息）
- `OnUILog`：可供 UI 层订阅实时日志输出

### 5.3 `LoggerLevelManager`

- `LogSwitch`：全局动态日志级别开关
- `SetLevel(...)`：运行时动态调节级别

### 5.4 WebAPI 扩展

- `UseGlobalExceptionHandler()`
- `UseRequestResponseLogging()`

### 5.5 WPF 扩展

- `SubscribeGlobalExceptions(...)`

---

## 6. 运维排障建议

- 看板无日志：先核对 `projectName == service.name`。
- 仅本地有日志：检查 `logs/fallback.log` 中 OTLP 回退告警。
- 端点恢复后仍无远端日志：
  - 检查 `AutoRecoverApplyForLogs`；
  - 检查认证头与端点地址；
  - 检查网络与端口连通性。
- 补传日志时间戳：补传事件使用**原始产生时间**（`entry.TimestampUtc`），而非补传执行时间。在 OpenObserve 中搜索 `ReplayQueue=true` 可定位所有补传日志。

---

## 7. 约束与建议

- 生产环境必须使用唯一 `projectName`。
- 推荐保留本地文件 sink 作为最终兜底。
- WebAPI 中间件顺序建议：全局异常在外层，请求响应日志紧随其后。
- 高并发场景建议配合动态级别控制，避免过量日志影响吞吐。
