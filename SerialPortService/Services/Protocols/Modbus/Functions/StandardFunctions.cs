namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// 读保持寄存器 (0x03)
    /// </summary>
    public class ReadHoldingRegisters : ModbusFunction
    {
        public override byte Code => 0x03;
        public override bool IsFixedLength => false;
        public override int LengthByteIndex => 0;
        public override int HeaderLength => 1;
    }

    /// <summary>
    /// 读输入寄存器 (0x04)
    /// </summary>
    public class ReadInputRegisters : ModbusFunction
    {
        public override byte Code => 0x04;
        public override bool IsFixedLength => false;
        public override int LengthByteIndex => 0;
        public override int HeaderLength => 1;
    }

    /// <summary>
    /// 写单个线圈 (0x05)
    /// </summary>
    public class WriteSingleCoil : ModbusFunction
    {
        public override byte Code => 0x05;
        public override bool IsFixedLength => true;
        public override int FixedDataLength => 4;
    }

    /// <summary>
    /// 写单个寄存器 (0x06)
    /// </summary>
    public class WriteSingleRegister : ModbusFunction
    {
        public override byte Code => 0x06;
        public override bool IsFixedLength => true;
        public override int FixedDataLength => 4;
    }

    /// <summary>
    /// 写多个寄存器 (0x10)
    /// </summary>
    public class WriteMultipleRegisters : ModbusFunction
    {
        public override byte Code => 0x10;
        public override bool IsFixedLength => true;
        public override int FixedDataLength => 4;
    }

    /// <summary>
    /// 异常响应 (0x80 + Code)
    /// </summary>
    public class ErrorFunction : ModbusFunction
    {
        public override byte Code => 0x80;
        public override bool IsFixedLength => true;
        public override int FixedDataLength => 1;
    }
}
