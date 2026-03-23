using SerialPortService.Models;

namespace SerialPortService.Services.Protocols.Modbus.Commands
{
    public sealed class WriteSingleCoilCommand : ModbusCommandBase<ModbusPacket>
    {
        private const ushort CoilOnValue = 0xFF00;
        private const ushort CoilOffValue = 0x0000;

        public WriteSingleCoilCommand(byte slaveId, ushort address, bool value)
            : base(slaveId, 0x05)
        {
            Address = address;
            Value = value;
        }

        public ushort Address { get; }
        public bool Value { get; }

        public override byte[] BuildRequest()
            => BuildFixedLengthRequest(SlaveId, FunctionCode, Address, Value ? CoilOnValue : CoilOffValue);

        public override ModbusPacket DecodeResponse(ModbusPacket response) => response;
    }
}
