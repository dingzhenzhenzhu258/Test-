using SerialPortService.Models;
using SerialPortService.Models.Enums;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 串口服务统一入口接口。
    /// 负责上下文创建、端口管理与发送能力对外暴露。
    /// </summary>
    public interface ISerialPortService
    {
        /// <summary>
        /// 自定义上下文工厂委托。
        /// </summary>
        public delegate IPortContext PortContextFactory(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory);

        /// <summary>
        /// 打开串口 (使用预定义设备枚举)
        /// </summary>
        /// <returns></returns>
        OperateResult OpenPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol = ProtocolEnum.Default);

        /// <summary>
        /// 打开串口 (使用自定义解析器)
        /// </summary>
        /// <typeparam name="T">解析结果类型</typeparam>
        /// <param name="parser">自定义解析器实例</param>
        OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class;

        /// <summary>
        /// 关闭串口
        /// </summary>
        Task<OperateResult> ClosePortAsync(string portName);

        /// <summary>
        /// 关闭所有已打开串口。
        /// </summary>
        OperateResult CloseAll();

        /// <summary>
        /// 获取已打开串口对应上下文。
        /// </summary>
        bool TryGetContext(string portName, out IPortContext? context);

        /// <summary>
        /// 注册设备处理器工厂。
        /// </summary>
        bool RegisterHandlerFactory(HandleEnum handleEnum, PortContextFactory factory);

        /// <summary>
        /// 设置设备到协议的动态解析器。
        /// </summary>
        void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver);

        /// <summary>
        /// 刷新串口
        /// </summary>
        /// <param name="oldPortNames"></param>
        void RefreshPorts(ref List<string> oldPortNames);

        /// <summary>
        /// 串口是否打开
        /// </summary>
        /// <param name="portName"></param>
        /// <returns></returns>
        bool IsOpen(string portName);

        /// <summary>
        /// 异常语义（必须成功）端口不存在或发送失败会抛异常，返回值是底层 IPortContext.Send 的回显字节数组
        /// </summary>
        /// <param name="portName"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        Task<byte[]> Write(string portName, byte[] data);

        /// <summary>
        /// 遇到高吞吐/超时问题，优先使用 TryWrite 并结合重试/退避与上面文档中的消费/通道调优建议
        /// 结果语义（推荐用于生产）
        /// 不会抛异常，返回 OperateResult<byte[]>；IsSuccess 表示是否发送成功，Content 为回显数据，Message 包含失败原因（例如“未打开”或“发送失败”）。
        /// </summary>
        /// <param name="portName"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        Task<OperateResult<byte[]>> TryWrite(string portName, byte[] data);
    }
}
