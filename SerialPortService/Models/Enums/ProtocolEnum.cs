namespace SerialPortService.Models.Enums
{
    /// <summary>
    /// 具体协议类型
    /// </summary>
    public enum ProtocolEnum
    {
        /// <summary>
        /// 使用设备默认协议。
        /// </summary>
        Default,

        /// <summary>
        /// 显式指定 Modbus RTU。
        /// </summary>
        ModbusRTU,

        /// <summary>
        /// 显式指定 Modbus ASCII。
        /// </summary>
        ModbusASCII
    }
}
