using System;

namespace SerialPortService.Models
{
    /// <summary>
    /// 协议不匹配异常（例如功能码、长度、格式不符合预期）。
    /// </summary>
    public sealed class ProtocolMismatchException : Exception
    {
        public ProtocolMismatchException(string message) : base(message) { }

        public ProtocolMismatchException(string message, Exception innerException) : base(message, innerException) { }
    }
}
