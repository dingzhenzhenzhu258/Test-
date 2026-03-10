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

        /// <summary>
        /// 步骤1：选择温湿度传感器设备类型。
        /// 为什么：用于路由到对应处理器与协议默认规则。
        /// 风险点：设备类型映射错误会导致上下文创建失败。
        /// </summary>
        TemperatureSensor,

        /// <summary>
        /// 步骤1：选择伺服电机设备类型。
        /// 为什么：区分业务语义并复用串口基础能力。
        /// 风险点：若协议推断不匹配，可能出现响应错配。
        /// </summary>
        ServoMotor,

        /// <summary>
        /// 步骤1：使用默认/通用设备类型。
        /// 为什么：支持自定义协议或外部工厂扩展。
        /// 风险点：未指定协议且无推断规则时会打开失败。
        /// </summary>
        Default
    }
}
