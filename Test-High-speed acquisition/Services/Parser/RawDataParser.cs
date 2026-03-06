using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace Test_High_speed_acquisition.Services.Parser
{
    public class RawDataParser : IStreamParser<byte[]>
    {
        public bool TryParse(byte b, out byte[]? result)
        {
            result = null;
            return false;
        }

        public void Parse(ReadOnlySpan<byte> data, List<byte[]> output)
        {
            if (data.Length == 0)
            {
                return;
            }

            output.Add(data.ToArray());
        }

        public void Reset()
        {
        }
    }
}
