using AvailableVerificationAlgorithms.Crc;
using SerialPortService.Models;
using System;

namespace SerialPortService.Services.Protocols.Modbus.Commands
{
    public sealed class WriteMultipleRegistersCommand : ModbusCommandBase<ModbusPacket>
    {
        public WriteMultipleRegistersCommand(byte slaveId, ushort startAddress, ushort[] values)
            : base(slaveId, 0x10)
        {
            StartAddress = startAddress;
            Values = values ?? throw new ArgumentNullException(nameof(values));
        }

        public ushort StartAddress { get; }
        public ushort[] Values { get; }

        public override byte[] BuildRequest()
        {
            var payloadLength = 7 + Values.Length * 2;
            var command = new byte[payloadLength + 2];
            command[0] = SlaveId;
            command[1] = FunctionCode;
            command[2] = (byte)(StartAddress >> 8);
            command[3] = (byte)(StartAddress & 0xFF);
            command[4] = (byte)(Values.Length >> 8);
            command[5] = (byte)(Values.Length & 0xFF);
            command[6] = (byte)(Values.Length * 2);

            var offset = 7;
            for (var i = 0; i < Values.Length; i++)
            {
                var value = Values[i];
                command[offset++] = (byte)(value >> 8);
                command[offset++] = (byte)(value & 0xFF);
            }

            var crc = Crc16Helpers.CalcCRC16(command.AsSpan(0, payloadLength));
            command[payloadLength] = (byte)(crc & 0xFF);
            command[payloadLength + 1] = (byte)(crc >> 8);
            return command;
        }

        public override ModbusPacket DecodeResponse(ModbusPacket response) => response;
    }
}
