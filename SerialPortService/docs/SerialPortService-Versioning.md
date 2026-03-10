# SerialPortService 版本与兼容性说明

## 语义化版本建议

- `Patch`：仅修复缺陷，不改变公开 API 行为。
- `Minor`：向后兼容新增（如新增 `TryWrite`）。
- `Major`：不兼容变更（删除/修改公开接口语义）。

## 当前变更记录（面向调用方）

- 新增：`ISerialPortService.TryWrite(string, byte[])`
- 调整：`PortContext.Send(byte[])` 在端口未打开时显式失败（抛异常）
- 增强：内置关键告警阈值（超时率、积压、重连失败率）

## 兼容性提示

- `ISerialPortService` 接口新增 `TryWrite` 属于 **Minor** 级别兼容新增。
- 若调用方直接实现了 `ISerialPortService`，需同步补齐该方法实现后再升级。
- 若调用方仅通过 DI 使用 `SerialPortServiceBase`，通常可直接升级并按需迁移到 `TryWrite`。

## 调用方升级建议

1. 生产业务优先迁移到 `TryWrite`。
2. 保留 `Write` 仅用于需要异常中断的路径。
3. 升级版本时同步更新配置项（`GenericHandlerOptions` 中告警阈值）。
