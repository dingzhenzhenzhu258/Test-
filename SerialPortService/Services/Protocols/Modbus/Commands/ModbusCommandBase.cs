using AvailableVerificationAlgorithms.Crc;
using SerialPortService.Models;
using SerialPortService.Services.Protocols;
using System;

namespace SerialPortService.Services.Protocols.Modbus.Commands
{
    /// <summary>
    /// Modbus 命令定义基类。
    /// </summary>
    public abstract class ModbusCommandBase<TResult> : IProtocolCommand<ModbusPacket, TResult>
    {
        protected ModbusCommandBase(byte slaveId, byte functionCode)
        {
            SlaveId = slaveId;
            FunctionCode = functionCode;
        }

        protected byte SlaveId { get; }
        protected byte FunctionCode { get; }

        public abstract byte[] BuildRequest();

        public virtual void ValidateResponse(ModbusPacket response)
        {
            ArgumentNullException.ThrowIfNull(response);

            if ((response.FunctionCode & 0x80) != 0)
            {
                if (response.Data.Length > 0)
                {
                    throw new ModbusException(response.Data[0], $"Modbus Error Code: {response.Data[0]}");
                }

                throw new ModbusException(null, "Modbus Error (Unknown Code)");
            }

            if (response.FunctionCode != FunctionCode)
            {
                throw new ProtocolMismatchException(
                    $"Unexpected Function Code: Expected 0x{FunctionCode:X2}, got 0x{response.FunctionCode:X2} (Raw: {BitConverter.ToString(response.RawFrame)})");
            }
        }

        public abstract TResult DecodeResponse(ModbusPacket response);

        protected static byte[] BuildFixedLengthRequest(byte slaveId, byte functionCode, ushort value1, ushort value2)
        {
            var frame = new byte[8];
            frame[0] = slaveId;
            frame[1] = functionCode;
            frame[2] = (byte)(value1 >> 8);
            frame[3] = (byte)(value1 & 0xFF);
            frame[4] = (byte)(value2 >> 8);
            frame[5] = (byte)(value2 & 0xFF);

            var crc = Crc16Helpers.CalcCRC16(frame.AsSpan(0, 6));
            frame[6] = (byte)(crc & 0xFF);
            frame[7] = (byte)(crc >> 8);
            return frame;
        }
    }
}
