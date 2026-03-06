using SerialPortService.Models;
using SerialPortService.Models.Emuns;
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
    public interface ISerialPortService
    {
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
        OperateResult ClosePort(string portName);

        OperateResult CloseAll();

        bool TryGetContext(string portName, out IPortContext? context);

        bool RegisterHandlerFactory(HandleEnum handleEnum, PortContextFactory factory);

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

        Task<byte[]> Write(string portName, byte[] data);
    }
}
