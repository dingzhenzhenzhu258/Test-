using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Modbus; // 新的命名空间
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// Modbus 处理器。
    /// 基于 <see cref="GenericHandler{T}"/> 复用通用收发能力，仅保留 Modbus 特有的响应匹配规则。
    /// 
    /// 【使用场景指南】
    /// 1. 主动式控制端 (推拉结合/主动请求)：
    ///    - 场景：作为 Client 向设备发起特定寄存器读写。
    ///    - 推荐 API: 使用 <see cref="SendRequestAsync"/>，该方法内部提供自动重试、超时控制，适合精准请求对应响应的流程验证。
    /// 2. 被动高频接收 (独立数据流)：
    ///    - 场景：处理设备主动上报（或高频次底层周期性拉取），单纯监听有效帧避免挤压阻塞。
    ///    - 推荐 API: 使用 <see cref="ReadParsedPacketsAsync"/> (<c>IAsyncEnumerable</c>)，业务只需 `await foreach` 即可连续消费完成解析的最新数据包，独立于发送请求的流控制。
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
            // 步骤1：复用通用发送核心流程。
            // 为什么：统一超时、重试、匹配、告警策略。
            // 风险点：若各协议各自实现，行为易分叉且难维护。
            => await SendRequestCoreAsync(command, timeout, retryCount, cancellationToken);

        /// <summary>
        /// 读取解析完成的 Modbus 报文流。
        /// 解析线程产出完整报文后写入通道，业务线程可独立异步消费。
        /// </summary>
        public override IAsyncEnumerable<ModbusPacket> ReadParsedPacketsAsync(CancellationToken cancellationToken = default)
            => base.ReadParsedPacketsAsync(cancellationToken);
    }
}
