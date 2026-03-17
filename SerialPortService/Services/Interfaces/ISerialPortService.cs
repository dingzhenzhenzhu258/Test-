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
        /// <param name="portName">串口名称</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="parity">校验位</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="stopBits">停止位</param>
        /// <param name="handleEnum">设备类型</param>
        /// <param name="protocol">协议类型</param>
        /// <param name="loggerFactory">日志工厂</param>
        /// <returns>创建好的串口上下文</returns>
        public delegate IPortContext PortContextFactory(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol, ILoggerFactory loggerFactory);

        /// <summary>
        /// 打开串口（使用预定义设备类型和协议）。
        /// </summary>
        /// <param name="portName">串口名称</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="parity">校验位</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="stopBits">停止位</param>
        /// <param name="handleEnum">设备类型</param>
        /// <param name="protocol">协议类型</param>
        /// <returns>打开结果</returns>
        OperateResult OpenPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol = ProtocolEnum.Default);

        /// <summary>
        /// 打开串口（使用自定义解析器）。
        /// </summary>
        /// <typeparam name="T">解析结果类型</typeparam>
        /// <param name="portName">串口名称</param>
        /// <param name="baudRate">波特率</param>
        /// <param name="parity">校验位</param>
        /// <param name="dataBits">数据位</param>
        /// <param name="stopBits">停止位</param>
        /// <param name="parser">自定义解析器实例</param>
        /// <returns>打开结果</returns>
        OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class;

        /// <summary>
        /// 关闭指定串口。
        /// </summary>
        /// <param name="portName">串口名称</param>
        /// <returns>关闭结果</returns>
        Task<OperateResult> ClosePortAsync(string portName);

        /// <summary>
        /// 关闭所有已打开串口。
        /// </summary>
        /// <returns>关闭结果</returns>
        OperateResult CloseAll();

        /// <summary>
        /// 获取已打开串口对应上下文。
        /// </summary>
        /// <param name="portName">串口名称</param>
        /// <param name="context">输出串口上下文</param>
        /// <returns>是否获取成功</returns>
        bool TryGetContext(string portName, out IPortContext? context);

        /// <summary>
        /// 注册设备处理器工厂。
        /// </summary>
        /// <param name="handleEnum">设备类型</param>
        /// <param name="factory">设备处理器工厂委托</param>
        /// <returns>是否注册成功</returns>
        bool RegisterHandlerFactory(HandleEnum handleEnum, PortContextFactory factory);

        /// <summary>
        /// 设置设备到协议的动态解析函数。
        /// </summary>
        /// <param name="resolver">设备类型到协议类型的推断函数</param>
        void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver);

        /// <summary>
        /// 刷新串口列表。
        /// </summary>
        /// <param name="oldPortNames">上一次缓存的串口名称列表</param>
        void RefreshPorts(ref List<string> oldPortNames);

        /// <summary>
        /// 判断指定串口是否已打开。
        /// </summary>
        /// <param name="portName">串口名称</param>
        /// <returns>是否已打开</returns>
        bool IsOpen(string portName);

        /// <summary>
        /// 以异常语义发送数据。
        /// 端口不存在或发送失败时抛出异常；返回值是底层 <see cref="IPortContext.Send(byte[])"/> 的回显字节数组。
        /// </summary>
        /// <param name="portName">串口名称</param>
        /// <param name="data">待发送数据</param>
        /// <returns>发送回显数据</returns>
        Task<byte[]> Write(string portName, byte[] data);

        /// <summary>
        /// 以结果语义发送数据（推荐用于生产）。
        /// 不抛异常，返回 <see cref="OperateResult{T}"/>；<c>IsSuccess</c> 表示是否发送成功，<c>Content</c> 为回显数据，<c>Message</c> 包含失败原因。
        /// </summary>
        /// <param name="portName">串口名称</param>
        /// <param name="data">待发送数据</param>
        /// <returns>发送结果</returns>
        Task<OperateResult<byte[]>> TryWrite(string portName, byte[] data);
    }
}
