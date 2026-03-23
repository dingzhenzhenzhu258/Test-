# SerialPortService Deployment Guide

## 目标

本文件用于把当前版本作为可交付候选版本部署到测试、联调或生产环境。

当前建议交付版本：

- `SerialPortService 1.0.0-rc.1`

## 部署前提

- 运行时：`.NET 8`
- Windows 主机具备目标串口访问权限
- 已安装目标设备驱动或虚拟串口驱动
- 应用可读取 `appsettings.json`
- 如启用远程日志，OTLP 端点可访问

## 推荐配置

只使用以下配置节：

- `SerialPortService:GenericHandlerOptions`
- `SerialPortService:RequestDefaults`

不要再使用：

- `SerialPortService:GenericHandler`

## 部署步骤

1. 构建发布版本。
2. 部署应用程序和配置文件。
3. 确认串口号、波特率、校验位、停止位与现场设备一致。
4. 启动应用并检查日志初始化是否成功。
5. 执行单口打开、发送、关闭冒烟验证。
6. 执行断线重连与端口重启验证。

## 上线后首轮验证

- 验证 `OpenPortAsync(...)` 成功
- 验证 `TryWrite(...)` 返回成功或可诊断失败
- 验证 `GetHealthSnapshot()` 返回正常状态
- 验证 `GetDiagnosticReport()` 有输出
- 验证 `RestartPortAsync(...)` 可正常重建端口
- 验证关闭流程不会卡住 UI 或宿主进程

## 推荐接入方式

- 设备型协议：`OpenPortAsync(portName, ..., handleEnum, protocol)`
- 自定义协议且需要支持重启：`OpenPortAsync<T>(..., Func<IStreamParser<T>> parserFactory)`
- 高速持续采集：优先 `ReadParsedPacketsAsync(...)`
- 一问一答控制：优先 `SendRequestAsync(...)`

## 常见问题

### 打不开串口

优先检查：

- 串口是否被其他程序占用
- 串口号是否正确
- 设备驱动是否正常
- 参数是否与设备一致

### 可以打开但无数据

优先检查：

- 波特率/校验位/停止位
- 发送端是否真的有数据
- parser 是否与协议匹配
- 是否正在消费 `ReadParsedPacketsAsync(...)`

### 长时间运行后异常

优先检查：

- `GetHealthSnapshot()`
- `GetDiagnosticReport()`
- 最近错误窗口
- backlog 与 timeout 指标

## 回滚建议

如果现场出现不可接受问题，回滚顺序建议如下：

1. 回滚应用程序版本
2. 回滚配置
3. 恢复为上一版串口接入方式
4. 保留本版日志与诊断导出文件用于分析
