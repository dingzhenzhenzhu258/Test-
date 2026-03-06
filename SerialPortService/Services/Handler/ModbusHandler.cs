using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Modbus; // 新的命名空间
using Microsoft.Extensions.Logging;
using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// Modbus 处理器。
    /// 基于 <see cref="GenericHandler{T}"/> 复用通用收发能力，
    /// 仅保留 Modbus 特有的响应匹配规则。
    /// </summary>
    public class ModbusHandler : GenericHandler<ModbusPacket>, IModbusContext
    {
        /// <summary>
        /// Modbus 请求/响应匹配策略。
        /// 按 SlaveId + FunctionCode（去异常位 0x80）进行匹配。
        /// </summary>
        private sealed class ModbusResponseMatcher : IResponseMatcher<ModbusPacket>
        {
            public bool IsResponseMatch(ModbusPacket response, byte[] command)
            {
                if (response == null || command == null || command.Length < 2) return false;

                byte slaveId = command[0];
                byte funcCode = command[1];
                byte actualFunc = (byte)(response.FunctionCode & 0x7F);
                return response.SlaveId == slaveId && actualFunc == funcCode;
            }

            public bool IsReportPacket(ModbusPacket response) => false;

            public void OnReportPacket(ModbusPacket response) { }

            public string BuildUnmatchedLog(ModbusPacket response)
                => $"Slave={response.SlaveId}, Func=0x{response.FunctionCode:X2}, Raw={BitConverter.ToString(response.RawFrame)}";
        }

        /// <summary>
        /// 创建 Modbus 处理器。
        /// </summary>
        public ModbusHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger, GenericHandlerOptions? options = null)
            : base(portName, baudRate, parity, dataBits, stopBits, new ModbusRtuParser(), logger, options, new ModbusResponseMatcher())
        {
        }

        /// <summary>
        /// 发送 Modbus 请求并等待响应 (同步转异步)
        /// </summary>
        /// <param name="command">完整的 Modbus 报文</param>
        /// <param name="timeout">超时时间 (毫秒)</param>
        /// <returns>响应报文</returns>
        public async Task<ModbusPacket> SendRequestAsync(byte[] command, int timeout = 1000, int retryCount = 3, CancellationToken cancellationToken = default)
            => await SendRequestCoreAsync(command, timeout, retryCount, cancellationToken);
    }
}
