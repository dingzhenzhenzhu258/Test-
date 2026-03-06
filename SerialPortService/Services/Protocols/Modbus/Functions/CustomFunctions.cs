namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// 自定义功能码 (0x44, 0x42, 0x45, 0x46)
    /// </summary>
    public class CustomFunction44 : ModbusFunction
    {
        public override byte Code => 0x44;

        public override bool IsFixedLength => false;
        
        // 0x44: [Addr] [44] [StartHi] [StartLo] [CountHi] [CountLo] [ByteCount] [Data...]
        // 长度字节是 ByteCount，索引为 4 (StartHi(0), StartLo(1), CountHi(2), CountLo(3), ByteCount(4))
        // 总长度 = 5 (Header) + ByteCount
        public override int LengthByteIndex => 4;
        public override int HeaderLength => 5;
    }
    
    // 其他类似 0x42, 0x45, 0x46 的也可以直接继承
    public class CustomFunction42 : CustomFunction44 { public override byte Code => 0x42; }
    public class CustomFunction45 : CustomFunction44 { public override byte Code => 0x45; }
    public class CustomFunction46 : CustomFunction44 { public override byte Code => 0x46; }

    /// <summary>
    /// 自定义功能码 (0x50)
    /// </summary>
    public class CustomFunction50 : ModbusFunction
    {
        public override byte Code => 0x50;

        public override bool IsFixedLength => false;
        
        // 0x50: [Addr] [50] [StartHi] [StartLo] [Count] [ByteCount] [Data...]
        // 长度字节是 ByteCount，索引为 3 (StartHi(0), StartLo(1), Count(2), ByteCount(3))
        // 总长度 = 4 (Header) + ByteCount
        public override int LengthByteIndex => 3;
        public override int HeaderLength => 4;
    }
}