# Logger 库使用说明

本库提供统一日志、追踪、指标接入能力，支持 OpenObserve 后台查看：

- Logs
- Metrics
- Traces

## 文档目录

- API 与接入说明：`docs/Logger-API-Usage.md`
- 运维与回退说明：`docs/Logger-Operations-Runbook.md`

## 快速开始

```csharp
services.AddSerilogLogging(configuration, projectName: "YourServiceName", isWebApi: false);
```

## 必须项（上线前）

- 每个服务都要设置唯一 `projectName`（对应 `service.name`）
- 不允许使用默认 `xxxapi`
- 确认 OTLP 地址与认证可达

## 已内置保障

- OTLP 不可达时自动回退到 `logs/fallback.log`
- 触发一次性告警，避免静默失败
- 统一 `AddLog` 扩展，携带方法名/文件/行号上下文

## 推荐调用方式

- WebAPI：
  - `AddSerilogLogging(..., isWebApi: true)`
  - `UseGlobalExceptionHandler()`
  - `UseRequestResponseLogging()`
- WPF/桌面应用：
  - `AddSerilogLogging(..., isWebApi: false)`
  - `SubscribeGlobalExceptions(...)`

## 常见问题

- 看板查不到日志：先确认 `projectName` 与筛选 `service.name` 一致
- 只有本地日志没有后台：检查 `logs/fallback.log` 是否有 OTLP 回退告警
