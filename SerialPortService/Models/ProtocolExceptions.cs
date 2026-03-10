using System;

namespace SerialPortService.Models
{
    /// <summary>
    /// 协议不匹配异常（例如功能码、长度、格式不符合预期）。
    /// </summary>
    public sealed class ProtocolMismatchException : Exception
    {
        /// <summary>
        /// 使用错误消息创建异常。
        /// </summary>
        public ProtocolMismatchException(string message) : base(message)
        {
        }

        /// <summary>
        /// 使用错误消息与内部异常创建异常。
        /// </summary>
        public ProtocolMismatchException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

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
