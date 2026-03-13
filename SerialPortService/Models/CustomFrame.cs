namespace SerialPortService.Models
{
    /// <summary>
    /// 自定义协议帧对象。
    /// </summary>
    public sealed class CustomFrame
    {
        public CustomFrame(byte command, byte[] payload, byte[] raw)
        {
            Command = command;
            Payload = payload;
            Raw = raw;
        }

        /// <summary>命令字。</summary>
        public byte Command { get; }

        /// <summary>负载数据。</summary>
        public byte[] Payload { get; }

        /// <summary>原始完整帧。</summary>
        public byte[] Raw { get; }
    }
}
