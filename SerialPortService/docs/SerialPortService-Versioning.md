# SerialPortService Versioning

## 版本语义

- `Patch`：缺陷修复，不改公开语义
- `Minor`：新增能力，但不破坏现有接口签名
- `Major`：删除旧模型、修改公开接口语义、移除历史兼容路径

## 当前版本的关键变更

### 已移除

- 旧的全局注册语义
- 旧的 `RegisterHandlerFactory(...)`
- 旧配置节 `SerialPortService:GenericHandler`

### 已新增

- `RegisterContextRegistration(...)` 返回 `ContextRegistrationResult`
- `RegisterParser<T>(...)` 返回 `ParserRegistrationResult`
- 实例级解析器注册表 `ParserFactory`
- 异步打开和关闭主路径

### 当前仍保留但应谨慎使用

- `OpenPort(...)`
- `OpenPort<T>(...)`
- `CloseAll()`

这些同步入口仍可用，但新代码应优先异步版本。

## 升级检查

1. 把配置节统一迁到 `SerialPortService:GenericHandlerOptions`
2. 删除所有旧的 `RegisterHandlerFactory(...)` 调用
3. 新增协议时优先实现 `IStreamParser<T>`，再通过 `RegisterParser<T>(...)` 注册
4. 新增设备时优先实现 `IPortContextRegistration`
5. 长跑采集场景优先走 `ReadParsedPacketsAsync(...)`
