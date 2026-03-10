# SerialPortService API 与使用说明

## 写入接口语义

- `Write(string portName, byte[] data)`
  - 语义：异常流。
  - 端口未打开、发送失败、参数非法时会抛异常。

- `TryWrite(string portName, byte[] data)`
  - 语义：结果流。
  - 返回 `OperateResult<byte[]>`，`IsSuccess=false` 时由业务方处理失败分支。

## 推荐调用方式

生产环境推荐：

1. 常规业务调用使用 `TryWrite`。
2. 对 `IsSuccess=false` 执行统一策略（限次重试、降级、告警、审计）。
3. 仅在需要中断流程时使用 `Write` 并捕获异常。

## 典型失败分支

- 串口未打开：`Message` 包含“未打开”。
- 底层发送失败：`Message` 包含“发送失败”。
- 入参非法：`Message` 包含“不能为空”等提示。

## OpenPort 约束说明

- 同一个串口名重复 `OpenPort` 时，若关键参数不一致（波特率、校验位、数据位、停止位、协议/解析器），库会返回失败。
- 建议流程：`ClosePort` 后再按新参数重新 `OpenPort`。

## 推荐异常处理模板

1. 优先走 `TryWrite`。
2. 失败时记录业务日志 + 设备标识 + 指令摘要。
3. 按业务策略执行限次重试和人工告警。
