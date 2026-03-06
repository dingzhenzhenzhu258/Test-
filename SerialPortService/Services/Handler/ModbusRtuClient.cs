using AvailableVerificationAlgorithms.Crc;
using Logger.Helpers;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// Modbus RTU 客户端助手。
    /// 在业务层提供易用的读写方法（03/06/10），
    /// 底层通过 <see cref="IModbusContext"/> 执行请求响应与重试控制。
    /// </summary>
    public class ModbusRtuClient
    {
        private readonly IModbusContext _handler;
        private readonly byte _slaveId;
        private readonly ILogger _logger;

        /// <summary>
        /// 创建 Modbus RTU 客户端。
        /// </summary>
        /// <param name="handler">Modbus 上下文</param>
        /// <param name="slaveId">默认从站地址</param>
        /// <param name="logger">日志实例（可空）</param>
        public ModbusRtuClient(IModbusContext handler, byte slaveId, ILogger logger = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _slaveId = slaveId;
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// 03 Read Holding Registers (读保持寄存器)
        /// </summary>
        /// <returns>寄存器数据 (字节数组，长度为 count*2)</returns>
        public async Task<byte[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count)
        {
            byte[] command = BuildCommand(_slaveId, 0x03, startAddress, count);
            _logger.AddLog(LogLevel.Information, $"[TX] {BitConverter.ToString(command)}");

            var response = await _handler.SendRequestAsync(command, 3000, 3);
            _logger.AddLog(LogLevel.Information, $"[RX] {BitConverter.ToString(response.RawFrame)}");

            // 异常检查
            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                    throw new Exception($"Modbus Error Code: {response.Data[0]}");
                else
                    throw new Exception($"Modbus Error (Unknown Code)");
            }

            // 检查功能码是否匹配
            if (response.FunctionCode != 0x03)
            {
                // 如果是 0x44 (写多个寄存器的响应)，我们是否应该容错？
                // 或者说，这是不是一次"串台"？
                // 根据您提供的文档，0x44 是"写多个寄存器"的功能码。
                // 如果我们发的是 03 (读)，却收到了 44 (写响应)，那说明：
                // 1. 我们发错了？(TX显示是03)
                // 2. 设备回错了？
                // 3. 收到了上一次请求的迟到响应？
                // 4. 收到了主动上报？(文档里没说44是主动上报，44是写响应)
                
                throw new Exception($"Unexpected Function Code: Expected 0x03, got 0x{response.FunctionCode:X2} (Raw: {BitConverter.ToString(response.RawFrame)})");
            }

            // 03 响应格式: [SlaveId] [03] [ByteCount] [DataHi] [DataLo] ... [CRC]
            // Parser 已经把 [DataHi] [DataLo] ... 放在 response.Data 里了吗？
            // 我们需要检查 ModbusRtuParser 的实现。
            // ModbusRtuParser 里的 Data 是 "数据域"，即去除 头(2) 和 尾(2) 的部分。
            // 对于 03 响应，Data[0] 是 ByteCount，Data[1..n] 是实际寄存器数据。
            
            if (response.Data.Length < 1)
                throw new Exception("Invalid response length");

            int byteCount = response.Data[0];
            if (response.Data.Length - 1 != byteCount)
                throw new Exception($"Data length mismatch. Expected {byteCount}, got {response.Data.Length - 1} (Raw: {BitConverter.ToString(response.Data)})");

            // 返回纯寄存器数据 (去头)
            return response.Data.Skip(1).ToArray();
        }

        /// <summary>
        /// 06 Write Single Register（写单个寄存器）。
        /// </summary>
        public async Task WriteSingleRegisterAsync(ushort address, ushort value)
        {
            byte[] command = BuildCommand(_slaveId, 0x06, address, value);
            _logger.AddLog(LogLevel.Information, $"[TX] {BitConverter.ToString(command)}");

            var response = await _handler.SendRequestAsync(command, 3000, 3);
            _logger.AddLog(LogLevel.Information, $"[RX] {BitConverter.ToString(response.RawFrame)}");

            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                    throw new Exception($"Modbus Error Code: {response.Data[0]}");
                else
                    throw new Exception($"Modbus Error (Unknown Code)");
            }

            // 检查功能码是否匹配
            if (response.FunctionCode != 0x06)
                throw new Exception($"Unexpected Function Code: Expected 0x06, got 0x{response.FunctionCode:X2} (Raw: {BitConverter.ToString(response.RawFrame)})");
                
            // 06 响应通常是回显请求，无需额外处理
        }

        /// <summary>
        /// 10 Write Multiple Registers（写多个寄存器）。
        /// </summary>
        public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values)
        {
            if (values == null || values.Length == 0) return;

            // 构造命令: SlaveId(1) + 0x10(1) + StartAddr(2) + RegCount(2) + ByteCount(1) + Data(n*2) + CRC(2)
            var frame = new List<byte>();
            frame.Add(_slaveId);
            frame.Add(0x10);
            frame.Add((byte)(startAddress >> 8));
            frame.Add((byte)(startAddress & 0xFF));
            frame.Add((byte)(values.Length >> 8));
            frame.Add((byte)(values.Length & 0xFF));
            frame.Add((byte)(values.Length * 2));

            foreach (var val in values)
            {
                frame.Add((byte)(val >> 8));
                frame.Add((byte)(val & 0xFF));
            }

            ushort crc = Crc16Helpers.CalcCRC16(frame.ToArray());
            frame.Add((byte)(crc & 0xFF));
            frame.Add((byte)(crc >> 8));

            byte[] command = frame.ToArray();
            _logger.AddLog(LogLevel.Information, $"[TX] {BitConverter.ToString(command)}");

            var response = await _handler.SendRequestAsync(command, 3000, 3);
            _logger.AddLog(LogLevel.Information, $"[RX] {BitConverter.ToString(response.RawFrame)}");

            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                    throw new Exception($"Modbus Error Code: {response.Data[0]}");
                else
                    throw new Exception($"Modbus Error (Unknown Code)");
            }

            if (response.FunctionCode != 0x10)
                throw new Exception($"Unexpected Function Code: Expected 0x10, got 0x{response.FunctionCode:X2}");
        }

        /// <summary>
        /// 批量优化写入：自动将连续地址合并为 0x10 命令
        /// </summary>
        /// <param name="writes">待写入的地址和值列表</param>
        public async Task BatchWriteAsync(IEnumerable<(ushort Address, ushort Value)> writes)
        {
            // 1. 排序
            var sortedWrites = writes.OrderBy(x => x.Address).ToList();
            if (sortedWrites.Count == 0) return;

            // 2. 分组连续地址
            var batchList = new List<List<(ushort Address, ushort Value)>>();
            var currentBatch = new List<(ushort Address, ushort Value)>();
            
            foreach (var item in sortedWrites)
            {
                if (currentBatch.Count == 0)
                {
                    currentBatch.Add(item);
                }
                else
                {
                    // 检查是否连续 (当前地址 == 上一个地址 + 1)
                    var last = currentBatch.Last();
                    if (item.Address == last.Address + 1)
                    {
                        currentBatch.Add(item);
                    }
                    else
                    {
                        // 不连续，结束当前批次
                        batchList.Add(currentBatch);
                        currentBatch = new List<(ushort Address, ushort Value)> { item };
                    }
                }
            }
            if (currentBatch.Count > 0) batchList.Add(currentBatch);

            // 3. 执行写入
            foreach (var batch in batchList)
            {
                if (batch.Count == 1)
                {
                    // 单个寄存器 -> 用 06
                    _logger.AddLog(LogLevel.Information, $">>> 单个写入: Addr=0x{batch[0].Address:X4} ({batch[0].Address}), Val={batch[0].Value}");
                    await WriteSingleRegisterAsync(batch[0].Address, batch[0].Value);
                }
                else
                {
                    // 多个连续寄存器 -> 用 10
                    ushort startAddr = batch[0].Address;
                    ushort[] values = batch.Select(x => x.Value).ToArray();
                    
                    var sb = new StringBuilder();
                    sb.Append($"Addr=0x{startAddr:X4} ({startAddr}), Count={values.Length} | Values: ");
                    for(int i=0; i<values.Length; i++)
                    {
                         sb.Append($"[0x{startAddr+i:X4}]={values[i]} ");
                    }
                    _logger.AddLog(LogLevel.Information, $">>> 批量写入: {sb}");

                    await WriteMultipleRegistersAsync(startAddr, values);
                }
            }
        }

        // --- 辅助方法 ---
        private byte[] BuildCommand(byte slaveId, byte funcCode, ushort addr, ushort val)
        {
            var frame = new List<byte>();
            frame.Add(slaveId);
            frame.Add(funcCode);
            frame.Add((byte)(addr >> 8));
            frame.Add((byte)(addr & 0xFF));
            frame.Add((byte)(val >> 8));
            frame.Add((byte)(val & 0xFF));
            
            // 注意：这里需要根据实际 CRC 帮助类调整
            // 原有代码使用的是 Crc16Helpers.CalcCRC16
            // Modbus RTU 通常是低位在前，高位在后
            ushort crc = Crc16Helpers.CalcCRC16(frame.ToArray());
            frame.Add((byte)(crc & 0xFF)); // Low
            frame.Add((byte)(crc >> 8));   // High

            return frame.ToArray();
        }
    }
}
