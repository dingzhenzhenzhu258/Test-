# SerialPortService 版本与兼容性说明

## 语义化版本建议

- `Patch`：仅修复缺陷，不改变公开 API 行为。
- `Minor`：向后兼容新增（如新增 `TryWrite`）。
- `Major`：不兼容变更（删除/修改公开接口语义）。

---

## 变更记录

### 结构性重构（当前版本）

> 以下变更**不影响公开 API 语义**，调用方无需修改业务代码。

#### 命名空间修复

- `SerialPortService.Models.Emuns` → `SerialPortService.Models.Enums`（拼写错误修正）
- 影响文件：`HandleEnum`、`ProtocolEnum`、`BaudRateEnum`、`DataBitsEnum`
- **升级动作**：将所有 `using SerialPortService.Models.Emuns` 替换为 `using SerialPortService.Models.Enums`

#### 文件夹分层重组

| 变动 | 旧路径 | 新路径 |
|------|--------|--------|
| Handler 三层分组 | `Handler/*.cs` | `Handler/Core/`、`Handler/Devices/`、`Handler/Metrics/` |
| Modbus 模型分组 | `Models/ModbusPacket.cs` | `Models/Modbus/ModbusPacket.cs` |
| Modbus 异常 | `Models/ModbusException.cs` | `Models/Modbus/ModbusException.cs` |
| 枚举合并 | `Models/Emuns/*.cs` | `Models/Enums/` |
| 报警枚举 | 内嵌在 `AudibleVisualAlarmHandler.cs` | `Models/Enums/AlarmEnums.cs` |
| 自定义协议帧 | `Services/Parser/CustomProtocol.cs`（含模型） | `Models/CustomFrame.cs` + `Services/Parser/CustomProtocolParser.cs` |

#### 类职责拆分

| 变动 | 说明 |
|------|------|
| `PortContext<T>` 拆为 3 个 partial 文件 | 生命周期 / Pipeline 循环 / 重连逻辑 |
| `SerialPortServiceBase` 提取 `PortContextFactory` | 设备路由与协议推断独立为 `PortContextFactory.cs` |
| `GenericHandlerMetricsPublisher` 提取接口 | `IGenericHandlerMetricsProvider` 独立为单文件 |

#### 接口清理

- 删除空接口 `IPortContext<T>`（无引用，无实际约束）

---

### API 新增（面向调用方）

- 新增：`ISerialPortService.TryWrite(string, byte[])` — 结果语义发送，不抛异常
- 调整：`PortContext.Send(byte[])` 在端口未打开时显式抛 `InvalidOperationException`
- 增强：内置三类告警（超时率、积压、重连失败率）

---

## 兼容性说明

### 本轮结构重构

- **Namespace 变更**：`Models.Emuns` → `Models.Enums`，调用方需更新 using（一行替换）
- **文件路径变更**：不影响编译，IDE 会自动重新索引
- **公开 API 不变**：`OpenPort` / `ClosePort` / `Write` / `TryWrite` 签名无变化

### `ISerialPortService` 接口新增 `TryWrite`

属于 **Minor** 级别兼容新增：
- 若调用方直接实现了 `ISerialPortService`，需补齐 `TryWrite` 方法后再升级
- 若仅通过 DI 使用 `SerialPortServiceBase`，可直接升级并按需迁移到 `TryWrite`

---

## 升级建议

1. 全局替换 `using SerialPortService.Models.Emuns` → `using SerialPortService.Models.Enums`
2. 生产业务优先迁移到 `TryWrite`，保留 `Write` 仅用于需要异常中断的路径
3. 同步更新配置项（`GenericHandlerOptions` 中告警阈值）
4. 若直接实现 `ISerialPortService`，补充 `TryWrite` 实现

---

## 升级检查清单

升级前后建议至少检查以下项目：

1. `using` 命名空间是否已从 `Models.Emuns` 全部替换到 `Models.Enums`
2. 是否存在直接实现 `ISerialPortService` 的自定义服务
3. 生产配置中的 `GenericHandlerOptions` 是否已补齐重连与告警阈值
4. 业务发送路径是否应从 `Write` 迁移到 `TryWrite`
5. 监控系统是否已接入 `SerialPortService.GenericHandler` 这个 `Meter`
6. 看板和告警是否已按 `port` / `deviceType` / `protocol` 标签拆分

---

## 按场景评估升级影响

### 1) 主动请求 / 一问一答

- 重点检查 `SendRequestAsync(...)` 调用链是否仍按原有超时与重试策略运行。
- 升级后建议重点观察：`timeout_count`、`retry_count`、`avg_latency_ms`。
- 若原来业务依赖“发送失败即抛异常”，可继续保留 `Write`；否则建议迁移到 `TryWrite`。

### 2) 被动持续采集 / 高频上报

- 重点检查消费者是否能跟上 `ReadParsedPacketsAsync(...)` 的产出速率。
- 升级后建议重点观察：`wait_backlog`、`wait_backlog_high_watermark`、`overflow_dropped`。
- 若现场是高吞吐链路，务必确认 `ResponseChannelFullMode`、容量与持久化 worker 配置匹配。

### 3) 仅发送命令 / 不关心匹配响应

- 重点检查业务方是否已经统一走 `TryWrite` 结果语义。
- 升级后建议重点观察发送失败分支、重连行为和失败日志是否接入告警。
- 若仍保留 `Write`，需确认调用方已经完整捕获异常并具备补偿逻辑。
