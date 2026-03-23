using SerialPortService.Models;
using System;

namespace SerialPortService.Services.Protocols.Modbus.Commands
{
    public sealed class ReadInputRegistersCommand : ModbusCommandBase<byte[]>
    {
        public ReadInputRegistersCommand(byte slaveId, ushort startAddress, ushort count)
            : base(slaveId, 0x04)
        {
            StartAddress = startAddress;
            Count = count;
        }

        public ushort StartAddress { get; }
        public ushort Count { get; }

        public override byte[] BuildRequest() => BuildFixedLengthRequest(SlaveId, FunctionCode, StartAddress, Count);

        public override byte[] DecodeResponse(ModbusPacket response)
        {
            if (response.Data.Length < 1)
            {
                throw new ProtocolMismatchException("Invalid response length");
            }

            var byteCount = response.Data[0];
            if (response.Data.Length - 1 != byteCount)
            {
                throw new ProtocolMismatchException(
                    $"Data length mismatch. Expected {byteCount}, got {response.Data.Length - 1} (Raw: {BitConverter.ToString(response.Data)})");
            }

            var registerData = new byte[byteCount];
            Array.Copy(response.Data, 1, registerData, 0, byteCount);
            return registerData;
        }
    }
}
