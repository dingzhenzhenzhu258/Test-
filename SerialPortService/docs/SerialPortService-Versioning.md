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
