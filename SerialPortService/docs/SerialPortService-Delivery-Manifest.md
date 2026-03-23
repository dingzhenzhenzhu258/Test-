# SerialPortService Delivery Manifest

## 当前交付版本

- `SerialPortService 1.0.0-rc.1`

## 交付内容

- 核心类库：`SerialPortService`
- 日志组件：`Logger`
- 示例应用：`Test-High-speed acquisition`
- 自动化测试：`InfraExtensions.Tests`
- API 文档
- 配置文档
- 指标 Runbook
- 虚拟串口验证文档
- 部署指南
- 发布检查表

## 当前版本特性

- 实例级注册表
- 实例级端口状态管理
- 自定义 parser factory 打开与重启
- 服务级健康快照与诊断报告
- 批量端口重启
- 高速采集背压控制
- 关闭超时保护
- 协议定义、命令执行、请求响应、持续采集分层

## 不包含内容

- 本仓库内未附带真实现场驱动安装包
- 本仓库内未附带自动化长跑压测报告
- 本仓库内未附带最终生产环境 OTLP 平台配置

## 建议交付流程

1. 使用当前版本进行现场联调
2. 完成冒烟和断线恢复验证
3. 固化现场配置
4. 再发布正式版号
