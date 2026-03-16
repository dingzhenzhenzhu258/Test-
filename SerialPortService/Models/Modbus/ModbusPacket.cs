using System;

namespace SerialPortService.Models
{
    /// <summary>
    /// Modbus数据包
    /// </summary>
    public class ModbusPacket
    {
        public byte SlaveId { get; set; }      // 从站地址 (区分不同设备)
        public byte FunctionCode { get; set; } // 功能码 (区分读/写等操作)
        public byte[] Data { get; set; } = Array.Empty<byte>();      // 数据域
        public byte[] RawFrame { get; set; } = Array.Empty<byte>();  // 原始完整报文

        // 辅助属性：判断是否是异常响应 (最高位为1)
        public bool IsError => (FunctionCode & 0x80) != 0;
    }
}
