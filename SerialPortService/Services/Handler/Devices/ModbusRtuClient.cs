using Logger.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SerialPortService.Models;
using SerialPortService.Options;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols;
using SerialPortService.Services.Protocols.Modbus.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        private const ushort CoilOnValue = 0xFF00;
        private const ushort CoilOffValue = 0x0000;

        private readonly IModbusContext _handler;
        private readonly byte _slaveId;
        private readonly ILogger _logger;
        private readonly ProtocolCommandClient<ModbusPacket> _commandClient;
        private readonly int _requestTimeoutMs;
        private readonly int _requestRetryCount;

        public ModbusRtuClient(
            IModbusContext handler,
            byte slaveId,
            ILogger? logger = null,
            int requestTimeoutMs = 3000,
            int requestRetryCount = 3)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            if (slaveId < MinSlaveId || slaveId > MaxSlaveId)
            {
                throw new ArgumentOutOfRangeException(nameof(slaveId), $"SlaveId range must be {MinSlaveId}-{MaxSlaveId}.");
            }

            if (requestTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requestTimeoutMs), "Request timeout must be greater than zero.");
            }

            if (requestRetryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requestRetryCount), "Request retry count must be greater than or equal to zero.");
            }

            _slaveId = slaveId;
            _logger = logger ?? NullLogger.Instance;
            _commandClient = new ProtocolCommandClient<ModbusPacket>(_handler, _logger, static packet => packet.RawFrame, "Modbus");
            _requestTimeoutMs = requestTimeoutMs;
            _requestRetryCount = requestRetryCount;
        }

        public ModbusRtuClient(
            IModbusContext handler,
            byte slaveId,
            RequestDefaultsOptions requestDefaults,
            ILogger? logger = null)
            : this(
                handler,
                slaveId,
                logger,
                requestDefaults?.TimeoutMs ?? throw new ArgumentNullException(nameof(requestDefaults)),
                requestDefaults.RetryCount)
        {
        }

        public async Task<byte[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
        {
            ValidateReadHoldingRegisters(startAddress, count);
            var command = new ReadHoldingRegistersCommand(_slaveId, startAddress, count);
            return await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken cancellationToken = default)
        {
            ValidateReadHoldingRegisters(startAddress, count);
            var command = new ReadInputRegistersCommand(_slaveId, startAddress, count);
            return await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken cancellationToken = default)
        {
            var command = new WriteSingleCoilCommand(_slaveId, address, value);
            var response = await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);

            if (response.Data.Length != 4)
            {
                throw new ProtocolMismatchException($"Unexpected coil response length: {response.Data.Length}");
            }

            var echoedAddress = (ushort)((response.Data[0] << 8) | response.Data[1]);
            var echoedValue = (ushort)((response.Data[2] << 8) | response.Data[3]);
            var expectedValue = value ? CoilOnValue : CoilOffValue;
            if (echoedAddress != address || echoedValue != expectedValue)
            {
                throw new ProtocolMismatchException(
                    $"Unexpected coil echo: expected addr=0x{address:X4}, value=0x{expectedValue:X4}; got addr=0x{echoedAddress:X4}, value=0x{echoedValue:X4}");
            }
        }

        public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
        {
            var command = new WriteSingleRegisterCommand(_slaveId, address, value);
            await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken cancellationToken = default)
        {
            ValidateWriteMultipleRegisters(startAddress, values);
            var command = new WriteMultipleRegistersCommand(_slaveId, startAddress, values);
            await ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }

        public async Task BatchWriteAsync(IEnumerable<(ushort Address, ushort Value)> writes, CancellationToken cancellationToken = default)
        {
            if (writes == null)
            {
                throw new ArgumentNullException(nameof(writes));
            }

            var sortedWrites = writes.OrderBy(x => x.Address).ToList();
            if (sortedWrites.Count == 0)
            {
                return;
            }

            var batchList = new List<List<(ushort Address, ushort Value)>>();
            var currentBatch = new List<(ushort Address, ushort Value)>();

            foreach (var item in sortedWrites)
            {
                if (currentBatch.Count == 0)
                {
                    currentBatch.Add(item);
                    continue;
                }

                var last = currentBatch.Last();
                if (item.Address == last.Address + 1)
                {
                    currentBatch.Add(item);
                    continue;
                }

                batchList.Add(currentBatch);
                currentBatch = new List<(ushort Address, ushort Value)> { item };
            }

            if (currentBatch.Count > 0)
            {
                batchList.Add(currentBatch);
            }

            foreach (var batch in batchList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (batch.Count == 1)
                {
                    _logger.AddLog(LogLevel.Information, string.Format(">>> 单个写入: Addr=0x{0:X4} ({0}), Val={1}", batch[0].Address, batch[0].Value));
                    await WriteSingleRegisterAsync(batch[0].Address, batch[0].Value, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var index = 0;
                while (index < batch.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunkCount = Math.Min((int)MaxWriteMultipleRegisterCount, batch.Count - index);
                    if (chunkCount == 1)
                    {
                        var single = batch[index];
                        _logger.AddLog(LogLevel.Information, string.Format(">>> 单个写入: Addr=0x{0:X4} ({0}), Val={1}", single.Address, single.Value));
                        await WriteSingleRegisterAsync(single.Address, single.Value, cancellationToken).ConfigureAwait(false);
                        index++;
                        continue;
                    }

                    var startAddr = batch[index].Address;
                    var values = new ushort[chunkCount];
                    for (var i = 0; i < chunkCount; i++)
                    {
                        values[i] = batch[index + i].Value;
                    }

                    var sb = new StringBuilder();
                    sb.Append($"Addr=0x{startAddr:X4} ({startAddr}), Count={values.Length} | Values: ");
                    for (var i = 0; i < values.Length; i++)
                    {
                        sb.Append($"[0x{startAddr + i:X4}]={values[i]} ");
                    }

                    _logger.AddLog(LogLevel.Information, string.Concat(">>> 批量写入: ", sb));
                    await WriteMultipleRegistersAsync(startAddr, values, cancellationToken).ConfigureAwait(false);
                    index += chunkCount;
                }
            }
        }

        private async Task<TResult> ExecuteCommandAsync<TResult>(IProtocolCommand<ModbusPacket, TResult> command, CancellationToken cancellationToken)
        {
            return await _commandClient
                .ExecuteAsync(command, _requestTimeoutMs, _requestRetryCount, cancellationToken)
                .ConfigureAwait(false);
        }

        private static void ValidateReadHoldingRegisters(ushort startAddress, ushort count)
        {
            if (count == 0 || count > MaxReadRegisterCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count), $"Read register count must be 1-{MaxReadRegisterCount}.");
            }

            var endAddress = startAddress + count - 1;
            if (endAddress > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(startAddress), "Read register range exceeds 0-65535.");
            }
        }

        private static void ValidateWriteMultipleRegisters(ushort startAddress, ushort[] values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (values.Length == 0 || values.Length > MaxWriteMultipleRegisterCount)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"Write multiple register count must be 1-{MaxWriteMultipleRegisterCount}.");
            }

            var endAddress = startAddress + values.Length - 1;
            if (endAddress > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(startAddress), "Write register range exceeds 0-65535.");
            }
        }
    }
}
