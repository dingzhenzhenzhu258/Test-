# Test-High-speed acquisition (串口高速采集平台)

简要说明
- 这是一个以串口（SerialPort）为核心的采集与处理解决方案，包含可复用的串口服务库、日志扩展（Serilog + OpenTelemetry 集成示例）和一个用于演示与快速验证的 WPF 客户端应用（`Test-High-speed acquisition`）。

主要组成
- `SerialPortService`：核心库，负责串口上下文管理、协议解析、设备处理器（Handler）以及并发/重连/限流策略。
  - 支持定制 `IStreamParser<T>` 解析器（内置 Modbus RTU、条码、自定义协议等）。
  - 提供 `ISerialPortService`/`SerialPortServiceBase` 统一 API：`OpenPort`、`ClosePortAsync`、`Write`、`TryWrite`、`TryGetContext` 等。
- `Logger`：日志与诊断模块，封装了 Serilog 与 OpenTelemetry 的集成，并提供 `LoggerHelper.AddLog` 之类的扩展以便统一写日志与可选 UI 推送。
- `Test-High-speed acquisition`：WPF 客户端示例程序，演示如何使用串口服务进行高频轮询采集、并行持久化、UI 展示与诊断。

核心功能亮点
- 可插拔的设备 Handler：通过 `RegisterHandlerFactory` 或实现内置 Handler（例如 `ModbusHandler`、`BarcodeScannerHandler`、`CustomProtocolHandler`）扩展新设备类型。
- 可替换的协议解析器：实现 `IStreamParser<T>` 即可将自定义帧解析器注入到上下文（示例：`CustomProtocolParser`）。
- 全局重连与告警策略：`GenericHandlerOptions` 和 `SerialPortReconnectPolicy` 支持进程级重连参数与指标告警阈值。
- 可靠的关闭流程：对物理驱动死锁场景有超时保护并强制剥离句柄避免界面阻塞。
- 日志与 UI 事件：`LoggerHelper` 支持结构化日志、异常堆栈记录和通过事件推送 UI 层展示。

快速开始（开发）
1. 克隆仓库并打开解决方案：
   - 仓库根目录包含多个项目：`SerialPortService`、`Logger`、`Test-High-speed acquisition` 等。
2. 还原并构建（适用于 .NET 8 / C# 12）：
   - 使用 Visual Studio 或 `dotnet build`。
3. 运行示例 WPF 客户端（`Test-High-speed acquisition`）以交互方式测试串口打开/采集功能。

示例代码片段（来自示例客户端）
- 打开串口（Modbus RTU）:
```csharp
_serialPortService.OpenPort("COM1", 115200, Parity.None, 8, StopBits.One, HandleEnum.Default, ProtocolEnum.ModbusRTU);
```
- 使用自定义解析器打开串口：
```csharp
_serialPortService.OpenPort("COM1", 115200, Parity.None, 8, StopBits.One, new CustomProtocolParser());
```
- 同步发送并等待回包（示例）：
```csharp
var response = await modbusContext.SendRequestAsync(command, timeout: 3000, retryCount: 1, cancellationToken);
```

配置
- 日志、OpenTelemetry、设备配置等可通过 `Logger/appsettings.json` 与 `SerialPortService/appsettings.serialport.*.json`（样例）进行调整。

日志可用性与自动恢复（OTLP）
- `Logger` 模块支持“启动探测 + 自动降级 + 自动恢复”：
  - 启动时不可达：日志自动降级到本地 `logs/fallback.log`；
  - 端点恢复后：可自动恢复远端日志上报；
  - `Tracing/Metrics` 可按配置保持导出器启用，端点恢复后自动继续上报。
- 关键配置位于 `Logger/appsettings.json`：
  - `Logger:Otlp:Enabled`：是否启用 OTLP 能力；
  - `Logger:Otlp:ProbeOnStartup`：是否在启动阶段探测可达性；
  - `Logger:Otlp:ProbeTimeoutMs`：探测超时时间；
  - `Logger:Otlp:AutoRecoverProbe`：是否启用后台恢复探测；
  - `Logger:Otlp:AutoRecoverProbeIntervalMs`：恢复探测周期；
  - `Logger:Otlp:AutoRecoverApplyForLogs`：端点恢复后是否自动热恢复日志上报；
  - `Logger:Otlp:AutoRecoverApplyForTelemetry`：`Tracing/Metrics` 是否保持自动恢复策略；
  - `Logger:Otlp:LogsEndpoint/TracesEndpoint/MetricsEndpoint`：各链路 OTLP 端点。

代码约定与开发注意事项
- 注释风格：使用项目约定的“步骤编号 + 为什么 + 风险点”注释模板来说明实现动机与风险点。
- 扩展方法：若调用 `LoggerHelper.AddLog` 等扩展方法，请确保引用 `using Logger.Helpers;`。
- 并发与资源释放：`SerialPortServiceBase` 使用内部锁与 `ConcurrentDictionary` 管理端口上下文，关闭流程（`ClosePortAsync`/`CloseAll`）有超时保护，新增 Handler 时请注意 `Dispose`/`Close` 行为。

测试
- 项目内含若干单元测试（见 `InfraExtensions.Tests` 等），可以运行测试以验证核心协议与工具函数。

参与贡献
- 欢迎提交 Issue 与 PR。贡献前请先同步代码风格，并在 PR 中描述变更动机与兼容性影响。

许可
- 请在仓库中查看根目录或各项目的 LICENSE 文件以了解许可条款。

联系方式
- 仓库问题请使用 GitHub Issues。

---

简短说明：此 README 旨在提供快速上手、模块分布与开发注意事项的总体概览。如需更详细的 API 文档或运行手册，请查看 `SerialPortService/docs` 下的各类文档。