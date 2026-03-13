namespace SerialPortService.Models.Enums
{
    /// <summary>
    /// 具体协议类型
    /// </summary>
    public enum ProtocolEnum
    {
        /// <summary>
        /// 步骤1：使用设备默认协议。
        /// 为什么：允许由服务层按设备类型动态推断协议。
        /// 风险点：若推断规则缺失，可能回落到不支持协议。
        /// </summary>
        Default,

        /// <summary>
        /// 步骤1：显式指定 Modbus RTU。
        /// 为什么：RTU 是当前主要稳定协议路径。
        /// 风险点：设备非 RTU 时会出现解析失败。
        /// </summary>
        ModbusRTU,

        /// <summary>
        /// 步骤1：显式指定 Modbus ASCII。
        /// 为什么：为后续 ASCII 协议扩展预留枚举位。
        /// 风险点：当前版本未实现 ASCII 解析器，使用会返回不支持错误。
        /// </summary>
        ModbusASCII
    }
}
