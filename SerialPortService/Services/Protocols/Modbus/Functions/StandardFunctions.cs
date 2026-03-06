namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// 读保持寄存器 (0x03)
    /// </summary>
    public class ReadHoldingRegisters : ModbusFunction
    {
        public override byte Code => 0x03;

        public override bool IsFixedLength => false;
        
        // 0x03: [Addr] [03] [ByteCount] [Data...]
        // 数据区以 ByteCount 开头 (index=0)
        // 总长度 = 1 (ByteCount) + Value
        public override int LengthByteIndex => 0;
        public override int HeaderLength => 1;
    }

    /// <summary>
    /// 写单个寄存器 (0x06)
    /// </summary>
    public class WriteSingleRegister : ModbusFunction
    {
        public override byte Code => 0x06;

        public override bool IsFixedLength => true;
        // 0x06: [Addr] [06] [RegHi] [RegLo] [ValHi] [ValLo]
        // 数据区长度 = 4
        public override int FixedDataLength => 4;
    }

    /// <summary>
    /// 写多个寄存器 (0x10)
    /// </summary>
    public class WriteMultipleRegisters : ModbusFunction
    {
        public override byte Code => 0x10;

        public override bool IsFixedLength => true;
        // 0x10: [Addr] [10] [StartHi] [StartLo] [CountHi] [CountLo]
        // 数据区长度 = 4
        public override int FixedDataLength => 4;
    }
    
    /// <summary>
    /// 异常响应 (0x80 + Code)
    /// </summary>
    public class ErrorFunction : ModbusFunction
    {
        public override byte Code => 0x80; // 这是一个特殊标记，实际使用时我们会通过掩码判断

        public override bool IsFixedLength => true;
        public override int FixedDataLength => 1; // 1字节错误码
    }
}