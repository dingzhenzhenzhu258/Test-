namespace SerialPortService.Models.Enums
{
    /// <summary>
    /// 具体业务设备
    /// </summary>
    public enum HandleEnum
    {
        /// <summary>
        /// 声光报警器处理器。
        /// </summary>
        AudibleVisualAlarmHandler,

        /// <summary>
        /// 扫码枪处理器。
        /// </summary>
        BarcodeScanner,

        /// <summary>
        /// 控制器处理器。
        /// </summary>
        Controller,

        /// <summary>
        /// 温湿度传感器处理器。
        /// </summary>
        TemperatureSensor,

        /// <summary>
        /// 伺服电机设备类型。
        /// </summary>
        ServoMotor,

        /// <summary>
        /// 默认或通用设备类型。
        /// </summary>
        Default
    }
}
