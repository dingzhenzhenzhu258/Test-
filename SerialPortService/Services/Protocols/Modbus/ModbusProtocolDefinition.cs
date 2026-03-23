using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System;

namespace SerialPortService.Services.Protocols.Modbus
{
    public sealed class ModbusProtocolDefinition : IProtocolDefinition<ModbusPacket>
    {
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

        public string Name => "Modbus RTU";

        public ProtocolEnum Protocol => ProtocolEnum.ModbusRTU;

        public IStreamParser<ModbusPacket> CreateParser() => new ModbusRtuParser();

        public IResponseMatcher<ModbusPacket> CreateResponseMatcher() => new ModbusResponseMatcher();

        public byte[] GetRawFrame(ModbusPacket packet) => packet.RawFrame;
    }
}
