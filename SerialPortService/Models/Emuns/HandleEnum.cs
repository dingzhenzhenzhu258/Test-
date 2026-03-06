using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Models.Emuns
{
    /// <summary>
    /// 具体业务设备
    /// </summary>
    public enum HandleEnum
    {
        AudibleVisualAlarmHandler,
        BarcodeScanner,
        Controller,

        // 新增设备 (底层都可能是 Modbus，但业务不同)
        TemperatureSensor,         // 温湿度传感器
        ServoMotor,                 // 伺服电机
        Default // 默认/通用设备
    }
}
