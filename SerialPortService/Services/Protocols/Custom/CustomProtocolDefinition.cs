using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Parser;
using System;

namespace SerialPortService.Services.Protocols.Custom
{
    public sealed class CustomProtocolDefinition : IProtocolDefinition<CustomFrame>
    {
        private sealed class CustomResponseMatcher : IResponseMatcher<CustomFrame>
        {
            public bool IsResponseMatch(CustomFrame response, byte[] command)
            {
                if (response == null || command == null || command.Length < 3)
                    return false;

                var expectedCommand = command[2];
                return response.Command == expectedCommand;
            }

            public bool IsReportPacket(CustomFrame response) => false;

            public void OnReportPacket(CustomFrame response) { }

            public string BuildUnmatchedLog(CustomFrame response)
                => $"Cmd=0x{response.Command:X2}, Raw={BitConverter.ToString(response.Raw)}";
        }

        public string Name => "Custom Protocol";

        public ProtocolEnum Protocol => ProtocolEnum.Default;

        public IStreamParser<CustomFrame> CreateParser() => new CustomProtocolParser();

        public IResponseMatcher<CustomFrame> CreateResponseMatcher() => new CustomResponseMatcher();

        public byte[] GetRawFrame(CustomFrame packet) => packet.Raw;
    }
}
