using AvailableVerificationAlgorithms.Crc;
using Logger.Helpers;
using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        private const ushort MinSlaveId = 1;
        private const ushort MaxSlaveId = 247;
        private const ushort MaxReadRegisterCount = 125;
        private const ushort MaxWriteMultipleRegisterCount = 123;

        private readonly IModbusContext _handler;
        private readonly byte _slaveId;
        private readonly ILogger _logger;

        private void LogHexFrame(LogLevel level, string prefix, byte[] frame)
        {
            if (_logger.IsEnabled(level))
            {
                _logger.AddLog(level, string.Concat(prefix, BitConverter.ToString(frame)));
            }
        }

        /// <summary>
        /// 创建 Modbus RTU 客户端。
        /// </summary>
        /// <param name="handler">Modbus 上下文</param>
        /// <param name="slaveId">默认从站地址</param>
        /// <param name="logger">日志实例（可空）</param>
        public ModbusRtuClient(IModbusContext handler, byte slaveId, ILogger logger = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            if (slaveId < MinSlaveId || slaveId > MaxSlaveId)
            {
                throw new ArgumentOutOfRangeException(nameof(slaveId), $"SlaveId range must be {MinSlaveId}-{MaxSlaveId}.");
            }

            _slaveId = slaveId;
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// 03 Read Holding Registers (读保持寄存器)
        /// </summary>
        /// <returns>寄存器数据 (字节数组，长度为 count*2)</returns>
        public async Task<byte[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
        {
            // 步骤1：校验读取参数。
            // 为什么：提前拦截越界请求，避免向设备发送非法报文。
            // 风险点：参数非法会导致设备异常响应或通信失败。
            ValidateReadHoldingRegisters(startAddress, count);

            // 步骤2：构建并发送 03 指令。
            // 为什么：读取保持寄存器是最常见采集路径。
            // 风险点：报文构建错误会导致响应匹配失败。
            byte[] command = BuildCommand(_slaveId, 0x03, startAddress, count);
            LogHexFrame(LogLevel.Information, "[TX] ", command);

            var response = await _handler.SendRequestAsync(command, 3000, 3, cancellationToken);
            LogHexFrame(LogLevel.Information, "[RX] ", response.RawFrame);

            // 步骤3：检查 Modbus 异常响应位。
            // 为什么：功能码高位 0x80 表示设备返回异常。
            // 风险点：忽略异常位会把错误帧当作正常业务数据。
            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                    throw new ModbusException(response.Data[0], $"Modbus Error Code: {response.Data[0]}");
                else
                    throw new ModbusException(null, "Modbus Error (Unknown Code)");
            }

            // 步骤4：校验响应功能码是否与请求匹配。
            // 为什么：防止串台或迟到响应污染当前请求结果。
            // 风险点：功能码错配会引发数据语义错误。
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
                
                throw new ProtocolMismatchException($"Unexpected Function Code: Expected 0x03, got 0x{response.FunctionCode:X2} (Raw: {BitConverter.ToString(response.RawFrame)})");
            }

            // 03 响应格式: [SlaveId] [03] [ByteCount] [DataHi] [DataLo] ... [CRC]
            // Parser 已经把 [DataHi] [DataLo] ... 放在 response.Data 里了吗？
            // 我们需要检查 ModbusRtuParser 的实现。
            // ModbusRtuParser 里的 Data 是 "数据域"，即去除 头(2) 和 尾(2) 的部分。
            // 对于 03 响应，Data[0] 是 ByteCount，Data[1..n] 是实际寄存器数据。
            
            // 步骤5：校验数据域长度并提取寄存器数据。
            // 为什么：03 响应需要先读 ByteCount 再截取真实数据。
            // 风险点：长度不一致会导致越界读取或数据污染。
            if (response.Data.Length < 1)
                throw new ProtocolMismatchException("Invalid response length");

            int byteCount = response.Data[0];
            if (response.Data.Length - 1 != byteCount)
                throw new ProtocolMismatchException($"Data length mismatch. Expected {byteCount}, got {response.Data.Length - 1} (Raw: {BitConverter.ToString(response.Data)})");

            // 返回纯寄存器数据 (去头)
            var registerData = new byte[byteCount];
            Array.Copy(response.Data, 1, registerData, 0, byteCount);
            return registerData;
        }

        /// <summary>
        /// 06 Write Single Register（写单个寄存器）。
        /// </summary>
        public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
        {
            // 步骤1：构建并发送 06 指令。
            // 为什么：单寄存器写入需使用固定长度报文。
            // 风险点：地址或值编码错误会直接写错寄存器。
            byte[] command = BuildCommand(_slaveId, 0x06, address, value);
            LogHexFrame(LogLevel.Information, "[TX] ", command);

            var response = await _handler.SendRequestAsync(command, 3000, 3, cancellationToken);
            LogHexFrame(LogLevel.Information, "[RX] ", response.RawFrame);

            // 步骤2：优先处理异常响应。
            // 为什么：异常位优先级高于功能码匹配。
            // 风险点：若顺序错误，异常帧可能被误判为协议错配。
            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                    throw new ModbusException(response.Data[0], $"Modbus Error Code: {response.Data[0]}");
                else
                    throw new ModbusException(null, "Modbus Error (Unknown Code)");
            }

            // 步骤3：校验功能码一致性。
            // 为什么：确认当前响应属于本次写请求。
            // 风险点：错配场景会污染业务写入结果。
            if (response.FunctionCode != 0x06)
                throw new ProtocolMismatchException($"Unexpected Function Code: Expected 0x06, got 0x{response.FunctionCode:X2} (Raw: {BitConverter.ToString(response.RawFrame)})");
                
            // 06 响应通常是回显请求，无需额外处理
        }

        /// <summary>
        /// 10 Write Multiple Registers（写多个寄存器）。
        /// </summary>
        public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
        {
            // 步骤1：校验批量写入参数。
            // 为什么：10 指令有严格寄存器数量上限。
            // 风险点：超限会触发协议异常或设备拒绝执行。
            ValidateWriteMultipleRegisters(startAddress, values);

            // 步骤2：按 10 指令格式组帧并追加 CRC。
            // 为什么：批量写入依赖正确的数据长度与校验。
            // 风险点：长度字段或 CRC 错误会导致整帧无效。
            var payloadLength = 7 + values.Length * 2;
            var command = new byte[payloadLength + 2];
            command[0] = _slaveId;
            command[1] = 0x10;
            command[2] = (byte)(startAddress >> 8);
            command[3] = (byte)(startAddress & 0xFF);
            command[4] = (byte)(values.Length >> 8);
            command[5] = (byte)(values.Length & 0xFF);
            command[6] = (byte)(values.Length * 2);

            var offset = 7;
            for (var i = 0; i < values.Length; i++)
            {
                var val = values[i];
                command[offset++] = (byte)(val >> 8);
                command[offset++] = (byte)(val & 0xFF);
            }

            var crc = Crc16Helpers.CalcCRC16(command.AsSpan(0, payloadLength));
            command[payloadLength] = (byte)(crc & 0xFF);
            command[payloadLength + 1] = (byte)(crc >> 8);
            LogHexFrame(LogLevel.Information, "[TX] ", command);

            // 步骤3：发送并校验响应功能码。
            // 为什么：确认设备已接受本次批量写入。
            // 风险点：若不校验，失败写入可能被误判为成功。
            var response = await _handler.SendRequestAsync(command, 3000, 3, cancellationToken);
            LogHexFrame(LogLevel.Information, "[RX] ", response.RawFrame);

            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                    throw new ModbusException(response.Data[0], $"Modbus Error Code: {response.Data[0]}");
                else
                    throw new ModbusException(null, "Modbus Error (Unknown Code)");
            }

            if (response.FunctionCode != 0x10)
                throw new ProtocolMismatchException($"Unexpected Function Code: Expected 0x10, got 0x{response.FunctionCode:X2}");
        }

        /// <summary>
        /// 批量优化写入：自动将连续地址合并为 0x10 命令
        /// </summary>
        /// <param name="writes">待写入的地址和值列表</param>
        public async Task BatchWriteAsync(IEnumerable<(ushort Address, ushort Value)> writes, CancellationToken cancellationToken = default)
        {
            // 步骤1：参数空值校验。
            // 为什么：避免后续排序和分批逻辑出现空引用异常。
            // 风险点：调用方传空集合引用会导致运行时崩溃。
            if (writes == null)
            {
                throw new ArgumentNullException(nameof(writes));
            }

            // 步骤2：按地址排序。
            // 为什么：连续地址分批依赖升序输入。
            // 风险点：无序输入会降低合批效率并增加发送帧数量。
            var sortedWrites = writes.OrderBy(x => x.Address).ToList();
            if (sortedWrites.Count == 0) return;

            // 步骤3：按连续地址分组。
            // 为什么：连续地址可合并为 10 指令降低通信开销。
            // 风险点：分组错误会导致写入地址错位。
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
                    // 步骤3.1：判断地址连续性。
                    // 为什么：连续地址才能安全合并。
                    // 风险点：误判连续会把不相关寄存器写入同一批次。
                    var last = currentBatch.Last();
                    if (item.Address == last.Address + 1)
                    {
                        currentBatch.Add(item);
                    }
                    else
                    {
                        // 步骤3.2：遇到断点，结束当前批次。
                        // 为什么：确保每批地址范围严格连续。
                        // 风险点：跨断点合并会破坏设备寄存器映射。
                        batchList.Add(currentBatch);
                        currentBatch = new List<(ushort Address, ushort Value)> { item };
                    }
                }
            }
            if (currentBatch.Count > 0) batchList.Add(currentBatch);

            // 步骤4：逐批执行写入。
            // 为什么：控制单帧大小并支持中途取消。
            // 风险点：单次批量过大可能触发设备拒绝或超时。
            foreach (var batch in batchList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (batch.Count == 1)
                {
                    // 步骤4.1：单寄存器用 06 指令。
                    // 为什么：协议语义明确，兼容性更好。
                    // 风险点：误用 10 指令会增加帧开销并影响可读性。
                    _logger.AddLog(LogLevel.Information, string.Format(">>> 单个写入: Addr=0x{0:X4} ({0}), Val={1}", batch[0].Address, batch[0].Value));
                    await WriteSingleRegisterAsync(batch[0].Address, batch[0].Value, cancellationToken);
                }
                else
                {
                    // 步骤4.2：多寄存器用 10 指令并按上限切块。
                    // 为什么：协议规定单帧最多 123 寄存器。
                    // 风险点：超限发送会导致设备返回异常或丢帧。
                    var index = 0;
                    while (index < batch.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var chunkCount = Math.Min((int)MaxWriteMultipleRegisterCount, batch.Count - index);
                        if (chunkCount == 1)
                        {
                            var single = batch[index];
                            _logger.AddLog(LogLevel.Information, string.Format(">>> 单个写入: Addr=0x{0:X4} ({0}), Val={1}", single.Address, single.Value));
                            await WriteSingleRegisterAsync(single.Address, single.Value, cancellationToken);
                            index++;
                            continue;
                        }

                        ushort startAddr = batch[index].Address;
                        var values = new ushort[chunkCount];
                        for (var i = 0; i < chunkCount; i++)
                        {
                            values[i] = batch[index + i].Value;
                        }

                        var sb = new StringBuilder();
                        sb.Append($"Addr=0x{startAddr:X4} ({startAddr}), Count={values.Length} | Values: ");
                        for (int i = 0; i < values.Length; i++)
                        {
                            sb.Append($"[0x{startAddr + i:X4}]={values[i]} ");
                        }
                        _logger.AddLog(LogLevel.Information, string.Concat(">>> 批量写入: ", sb));

                        await WriteMultipleRegistersAsync(startAddr, values, cancellationToken);
                        index += chunkCount;
                    }
                }
            }
        }

        private static void ValidateReadHoldingRegisters(ushort startAddress, ushort count)
        {
            // 步骤1：校验读取数量范围。
            // 为什么：Modbus 03 单次最多 125 个寄存器。
            // 风险点：超限会触发设备异常响应。
            if (count == 0 || count > MaxReadRegisterCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Read register count must be 1-{MaxReadRegisterCount}.");
            }

            // 步骤2：校验地址范围不越界。
            // 为什么：避免 endAddress 超出 0~65535。
            // 风险点：地址越界会造成非法请求。
            var endAddress = startAddress + count - 1;
            if (endAddress > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(startAddress), "Read register range exceeds 0-65535.");
            }
        }

        private static void ValidateWriteMultipleRegisters(ushort startAddress, ushort[] values)
        {
            // 步骤1：校验入参数组。
            // 为什么：后续长度与地址计算依赖有效数组。
            // 风险点：空数组或空引用会导致构帧失败。
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            // 步骤2：校验数量范围。
            // 为什么：10 指令单次最多 123 寄存器。
            // 风险点：超限会触发协议异常。
            if (values.Length == 0 || values.Length > MaxWriteMultipleRegisterCount)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"Write multiple register count must be 1-{MaxWriteMultipleRegisterCount}.");
            }

            // 步骤3：校验地址终点不越界。
            // 为什么：保证请求地址合法。
            // 风险点：越界地址可能被设备拒绝或写错区域。
            var endAddress = startAddress + values.Length - 1;
            if (endAddress > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(startAddress), "Write register range exceeds 0-65535.");
            }
        }

        // --- 辅助方法 ---
        private byte[] BuildCommand(byte slaveId, byte funcCode, ushort addr, ushort val)
        {
            // 步骤1：按 Modbus RTU 固定格式写入帧头和数据域。
            // 为什么：03/06 指令均可复用该通用构帧逻辑。
            // 风险点：字段顺序错误会导致从站无法解析。
            var frame = new byte[8];
            frame[0] = slaveId;
            frame[1] = funcCode;
            frame[2] = (byte)(addr >> 8);
            frame[3] = (byte)(addr & 0xFF);
            frame[4] = (byte)(val >> 8);
            frame[5] = (byte)(val & 0xFF);

            // 步骤2：计算并写入 CRC（低字节在前）。
            // 为什么：RTU 帧完整性依赖 CRC 校验。
            // 风险点：CRC 错误会导致设备直接丢弃报文。
            var crc = Crc16Helpers.CalcCRC16(frame.AsSpan(0, 6));
            frame[6] = (byte)(crc & 0xFF);
            frame[7] = (byte)(crc >> 8);

            return frame;
        }
    }
}
