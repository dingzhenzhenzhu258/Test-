namespace SerialPortService.Models.Enums
{
    /// <summary>
    /// 光电报警器 LED 模式。
    /// </summary>
    public enum LedMode : byte
    {
        Off = 0x01,
        Green = 0x02,
        Yellow = 0x03,
        Red = 0x04
    }

    /// <summary>
    /// 光电报警器蜂鸣器模式。
    /// </summary>
    public enum BuzzerMode : byte
    {
        Off = 0x01,
        On = 0x02
    }

    /// <summary>
    /// 光电报警器闪光频率。
    /// </summary>
    public enum FlashFrequency : byte
    {
        Flash_off = 0x01,
        Flash_085s = 0x02,
        Flash_17s = 0x03,
        Flash_25s = 0x04
    }
}
