namespace SerialPortService.Models
{
    /// <summary>
    /// 发送通道中的原始数据包。
    /// </summary>
    /// <param name="Data">待发送的原始字节数组</param>
    public record DataPacket(byte[] Data);
}
