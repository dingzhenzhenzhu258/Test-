using SerialPortService.Models;

namespace SerialPortService.Services.Protocols.Modbus.Commands
{
    public sealed class WriteSingleRegisterCommand : ModbusCommandBase<ModbusPacket>
    {
        public WriteSingleRegisterCommand(byte slaveId, ushort address, ushort value)
            : base(slaveId, 0x06)
        {
            Address = address;
            Value = value;
        }

        public ushort Address { get; }
        public ushort Value { get; }

        public override byte[] BuildRequest() => BuildFixedLengthRequest(SlaveId, FunctionCode, Address, Value);

        public override ModbusPacket DecodeResponse(ModbusPacket response) => response;
    }
}
