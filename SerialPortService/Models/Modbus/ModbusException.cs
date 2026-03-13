using System;

namespace SerialPortService.Models
{
    /// <summary>
    /// Modbus 异常响应（功能码带 0x80）对应的业务异常。
    /// </summary>
    public sealed class ModbusException : Exception
    {
        /// <summary>
        /// Modbus 异常码。
        /// </summary>
        public byte? ErrorCode { get; }

        /// <summary>
        /// 使用异常码与消息创建 Modbus 异常。
        /// </summary>
        public ModbusException(byte? errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}
