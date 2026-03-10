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
            // 步骤1：按协议枚举分派解析器实现。
            // 为什么：把协议选择逻辑集中在工厂层，调用方无需关心具体实现。
            // 风险点：协议枚举与实现不一致时会在运行时抛出不支持异常。
            return protocol switch
            {
                ProtocolEnum.ModbusRTU => new ModbusRtuParser(),

                ProtocolEnum.ModbusASCII => throw new NotSupportedException("当前版本尚未实现 ModbusASCII 解析器，请改用 ModbusRTU 或自定义解析器"),

                // 未来如果你写了 ASCII 解析器，就在这里加一行，不用改主服务代码
                // ProtocolEnum.ModbusASCII => new ModbusAsciiParser(), 

                _ => throw new NotSupportedException($"工厂无法创建协议: {protocol}. 当前仅支持: ModbusRTU")
            };
        }

        /// <summary>
        /// 创建自定义协议解析器。
        /// </summary>
        public static IStreamParser<CustomFrame> CreateCustomProtocolParser()
        {
            // 步骤1：返回自定义协议解析器实例。
            // 为什么：统一由工厂负责解析器创建，便于后续替换实现。
            // 风险点：若业务直接 new 多种解析器，维护和升级成本会增加。
            return new CustomProtocolParser();
        }

        // 未来如果有其他类型的解析器（比如返回 string 的），也可以加在这里
        // public static IStreamParser<string> CreateStringParser(...) { ... }

        // 如果未来有其他类型的解析器（比如返回 string 的），也可以加在这里
        // public static IStreamParser<string> CreateStringParser(...) { ... }
    }
}
