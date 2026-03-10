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
    /// 基于 <see cref="GenericHandler{T}"/> 复用通用收发能力，
    /// 仅保留 Modbus 特有的响应匹配规则。
    /// </summary>
    public class ModbusHandler : GenericHandler<ModbusPacket>, IModbusContext
    {
        private readonly Channel<ModbusPacket> _parsedPacketChannel;
        private long _parsedPacketDropCount;

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
            var capacity = (options?.ResponseChannelCapacity ?? 4096);
            if (capacity <= 0)
            {
                capacity = 4096;
            }

            _parsedPacketChannel = Channel.CreateBounded<ModbusPacket>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = false
            });
        }

        /// <summary>
        /// 解析完成后直接分发到请求响应链路和业务通道，避免事件包装开销。
        /// </summary>
        protected override void OnParsed(ModbusPacket content)
        {
            // 步骤1：先走基类解析后处理逻辑。
            // 为什么：复用通用请求响应匹配、统计和分流能力。
            // 风险点：若跳过基类逻辑，会丢失重试/超时等关键统计。
            base.OnParsed(content);

            // 步骤2：尝试写入业务消费通道。
            // 为什么：提供独立的解析报文流给上层异步消费。
            // 风险点：通道满载时若不处理，会静默丢包且难以定位。
            if (_parsedPacketChannel.Writer.TryWrite(content))
            {
                return;
            }

            // 步骤3：记录丢包计数并按采样打印告警。
            // 为什么：在高压场景下可观测消费侧是否跟不上解析速率。
            // 风险点：无丢包告警会掩盖吞吐瓶颈问题。
            var dropped = Interlocked.Increment(ref _parsedPacketDropCount);
            if (dropped % 1000 == 0)
            {
                Logger.LogWarning("[{Handler}] Parsed packet channel dropped count: {Dropped}", nameof(ModbusHandler), dropped);
            }
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
        public IAsyncEnumerable<ModbusPacket> ReadParsedPacketsAsync(CancellationToken cancellationToken = default)
            => _parsedPacketChannel.Reader.ReadAllAsync(cancellationToken);

        /// <summary>
        /// 释放资源并停止报文通道。
        /// </summary>
        public override void Dispose()
        {
            _parsedPacketChannel.Writer.TryComplete();
            base.Dispose();
        }
    }
}
