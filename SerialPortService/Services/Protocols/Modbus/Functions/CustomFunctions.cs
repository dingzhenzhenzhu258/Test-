namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// 自定义功能码 (0x44, 0x42, 0x45, 0x46)
    /// </summary>
    public class CustomFunction44 : ModbusFunction
    {
        public override byte Code => 0x44;

        public override bool IsFixedLength => false;

        // 步骤1：声明 0x44 为变长响应并给出长度字节位置。
        // 为什么：该协议头部后含 ByteCount，需按索引提取长度。
        // 风险点：索引设置错误会导致变长帧截断或越界。
        public override int LengthByteIndex => 4;
        public override int HeaderLength => 5;
    }

    // 步骤1：复用 0x44 长度规则定义同构功能码。
    // 为什么：0x42/0x45/0x46 帧结构一致，仅功能码不同。
    // 风险点：若后续协议分叉，需拆分独立规则避免误解析。
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

        // 步骤1：声明 0x50 变长规则及长度字节索引。
        // 为什么：0x50 的头部字段数量与 0x44 不同。
        // 风险点：沿用 0x44 索引会导致长度计算偏移。
        public override int LengthByteIndex => 3;
        public override int HeaderLength => 4;
    }


}