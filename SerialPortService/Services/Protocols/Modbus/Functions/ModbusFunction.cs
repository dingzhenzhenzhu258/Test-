namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// Modbus 功能码基类
    /// </summary>
    public abstract class ModbusFunction
    {
        /// <summary>
        /// 功能码。
        /// </summary>
        public abstract byte Code { get; }

        /// <summary>
        /// 指示响应数据区是否为固定长度（不含 CRC）。
        /// </summary>
        public virtual bool IsFixedLength => true;

        /// <summary>
        /// 固定长度响应的数据区长度（不含 CRC）。
        /// </summary>
        public virtual int FixedDataLength => 0;

        /// <summary>
        /// 变长响应中长度字节所在的索引（相对于数据区起始位置）。
        /// 例如 `0x03` 的长度字节位于第 `0` 个数据字节，
        /// `0x44` 的长度字节位于第 `4` 个数据字节。
        /// </summary>
        public virtual int LengthByteIndex => 0;

        /// <summary>
        /// 变长响应中长度字节之前的头部长度（包含长度字节本身）。
        /// 用于计算总长度：`HeaderLength + LengthByteValue`。
        /// </summary>
        public virtual int HeaderLength => 0;
    }
}