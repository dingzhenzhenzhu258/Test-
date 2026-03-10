namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// 读保持寄存器 (0x03)
    /// </summary>
    public class ReadHoldingRegisters : ModbusFunction
    {
        public override byte Code => 0x03;

        public override bool IsFixedLength => false;

        // 步骤1：声明 0x03 为变长响应。
        // 为什么：ByteCount 字段决定后续数据区真实长度。
        // 风险点：误按定长解析会导致截断或等待超时。
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
        // 步骤1：声明 0x06 为定长响应，数据区固定 4 字节。
        // 为什么：06 响应为请求回显，长度固定。
        // 风险点：若误设为变长，会导致状态机长度判断错误。
        public override int FixedDataLength => 4;
    }

    /// <summary>
    /// 写多个寄存器 (0x10)
    /// </summary>
    public class WriteMultipleRegisters : ModbusFunction
    {
        public override byte Code => 0x10;

        public override bool IsFixedLength => true;
        // 步骤1：声明 0x10 为定长响应，数据区固定 4 字节。
        // 为什么：写多个寄存器响应只回显起始地址与数量。
        // 风险点：长度配置错误会影响后续帧边界判定。
        public override int FixedDataLength => 4;
    }
    
    /// <summary>
    /// 异常响应 (0x80 + Code)
    /// </summary>
    public class ErrorFunction : ModbusFunction
    {
        // 步骤1：以 0x80 作为异常响应规则标识。
        // 为什么：解析器通过功能码高位掩码统一分派到该规则。
        // 风险点：若无统一异常规则，异常帧会被当作未知功能码。
        public override byte Code => 0x80;

        public override bool IsFixedLength => true;
        // 步骤2：异常响应数据区固定 1 字节错误码。
        // 为什么：Modbus 异常帧规范仅返回异常码。
        // 风险点：长度配置不正确会导致 CRC 边界判断失败。
        public override int FixedDataLength => 1;
    }
}