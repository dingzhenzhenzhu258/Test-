using System;

namespace SerialPortService.Models
{
    /// <summary>
    /// 协议不匹配异常（例如功能码、长度、格式不符合预期）。
    /// </summary>
    public sealed class ProtocolMismatchException : Exception
    {
        /// <summary>
        /// 创建协议不匹配异常。
        /// </summary>
        /// <param name="message">异常说明</param>
        public ProtocolMismatchException(string message) : base(message) { }

        /// <summary>
        /// 创建协议不匹配异常。
        /// </summary>
        /// <param name="message">异常说明</param>
        /// <param name="innerException">内部异常</param>
        public ProtocolMismatchException(string message, Exception innerException) : base(message, innerException) { }
    }
}
