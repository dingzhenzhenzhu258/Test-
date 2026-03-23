# SerialPortService Release Checklist

## 构建

- `dotnet build SerialPortService/SerialPortService.csproj -nodeReuse:false`
- `dotnet build "Test-High-speed acquisition/Test-High-speed acquisition.csproj" -nodeReuse:false`
- `dotnet test InfraExtensions.Tests/InfraExtensions.Tests.csproj -nodeReuse:false`

## 配置

- 仅使用 `SerialPortService:GenericHandlerOptions`
- 已确认 `RequestDefaults` 合理
- 已确认日志配置可用
- 已确认 OTLP 配置符合现场要求

## 现场参数

- 串口号已确认
- 波特率已确认
- 数据位已确认
- 校验位已确认
- 停止位已确认

## 功能验收

- 打开端口成功
- 关闭端口成功
- 重启端口成功
- 发送请求成功
- 持续采集成功
- 健康快照可读取
- 诊断报告可导出

## 异常恢复

- 拔线/断开设备后可观测
- 恢复连接后可重连
- 关闭超时不会拖死进程
- 自定义 parser 端口可重启

## 文档

- API 用法文档已同步
- 场景配置文档已同步
- 部署说明已提供
- 交付清单已提供
