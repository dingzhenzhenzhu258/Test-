using System;

namespace SerialPortService.Models
{
    /// <summary>
    /// Modbus数据包
    /// </summary>
    public class ModbusPacket
    {
        /// <summary>
        /// 从站地址。
        /// </summary>
        public byte SlaveId { get; set; }

        /// <summary>
        /// 功能码。
        /// </summary>
        public byte FunctionCode { get; set; }

        /// <summary>
        /// 数据区（不含从站地址、功能码和 CRC）。
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 原始完整报文。
        /// </summary>
        public byte[] RawFrame { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 指示当前报文是否为异常响应。
        /// </summary>
        public bool IsError => (FunctionCode & 0x80) != 0;
    }
}
