namespace SerialPortService.Services.Protocols.Modbus.Functions
{
    /// <summary>
    /// Modbus 功能码基类
    /// </summary>
    public abstract class ModbusFunction
    {
        /// <summary>
        /// 功能码
        /// </summary>
        public abstract byte Code { get; }

        /// <summary>
        /// 是否为固定长度响应 (除了 CRC)
        /// </summary>
        public virtual bool IsFixedLength => true;

        /// <summary>
        /// 固定长度响应的数据区长度 (不包括 CRC)
        /// </summary>
        public virtual int FixedDataLength => 0;

        /// <summary>
        /// 变长响应中，长度字节所在的索引 (相对于数据区起始位置)
        /// 例如 0x03，长度字节是第 0 个字节
        /// 例如 0x44，长度字节是第 4 个字节 (前4字节是头)
        /// </summary>
        public virtual int LengthByteIndex => 0;

        /// <summary>
        /// 变长响应中，长度字节前面的头部长度 (包括长度字节本身)
        /// 用于计算总长度 = HeaderLength + [LengthByteValue]
        /// </summary>
        public virtual int HeaderLength => 0;
    }
}