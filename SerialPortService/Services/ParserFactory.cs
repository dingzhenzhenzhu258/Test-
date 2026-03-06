using SerialPortService.Models;
using SerialPortService.Models.Emuns;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Modbus;
using SerialPortService.Services.Parser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Services
{
    /// <summary>
    /// 解析器工厂：专门负责"生产"各种协议解析器
    /// </summary>
    public static class ParserFactory
    {
        /// <summary>
        /// 根据协议枚举，创建 Modbus 系列的解析器
        /// </summary>
        public static IStreamParser<ModbusPacket> CreateModbusParser(ProtocolEnum protocol)
        {
            return protocol switch
            {
                ProtocolEnum.ModbusRTU => new ModbusRtuParser(),

                // 未来如果你写了 ASCII 解析器，就在这里加一行，不用改主服务代码
                // ProtocolEnum.ModbusASCII => new ModbusAsciiParser(), 

                _ => throw new NotSupportedException($"工厂无法创建协议: {protocol}")
            };
        }

        public static IStreamParser<CustomFrame> CreateCustomProtocolParser()
        {
            return new CustomProtocolParser();
        }

        // 如果未来有其他类型的解析器（比如返回 string 的），也可以加在这里
        // public static IStreamParser<string> CreateStringParser(...) { ... }
    }
}
