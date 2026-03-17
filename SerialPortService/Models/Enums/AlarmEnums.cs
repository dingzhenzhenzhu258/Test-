namespace SerialPortService.Models.Enums
{
    /// <summary>
    /// 光电报警器 LED 模式。
    /// </summary>
    public enum LedMode : byte
    {
        /// <summary>
        /// 关闭 LED。
        /// </summary>
        Off = 0x01,

        /// <summary>
        /// 绿灯常亮。
        /// </summary>
        Green = 0x02,

        /// <summary>
        /// 黄灯常亮。
        /// </summary>
        Yellow = 0x03,

        /// <summary>
        /// 红灯常亮。
        /// </summary>
        Red = 0x04
    }

    /// <summary>
    /// 光电报警器蜂鸣器模式。
    /// </summary>
    public enum BuzzerMode : byte
    {
        /// <summary>
        /// 关闭蜂鸣器。
        /// </summary>
        Off = 0x01,

        /// <summary>
        /// 打开蜂鸣器。
        /// </summary>
        On = 0x02
    }

    /// <summary>
    /// 光电报警器闪光频率。
    /// </summary>
    public enum FlashFrequency : byte
    {
        /// <summary>
        /// 关闭闪光。
        /// </summary>
        Flash_off = 0x01,

        /// <summary>
        /// 约 0.85 秒闪烁一次。
        /// </summary>
        Flash_085s = 0x02,

        /// <summary>
        /// 约 1.7 秒闪烁一次。
        /// </summary>
        Flash_17s = 0x03,

        /// <summary>
        /// 约 2.5 秒闪烁一次。
        /// </summary>
        Flash_25s = 0x04
    }
}
