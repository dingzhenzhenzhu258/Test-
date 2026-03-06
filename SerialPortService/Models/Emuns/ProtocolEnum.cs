using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Models.Emuns
{
    /// <summary>
    /// 具体协议类型
    /// </summary>
    public enum ProtocolEnum
    {
        Default,    // 使用设备默认的协议
        ModbusRTU,   // 强制使用 Modbus RTU
        ModbusASCII // Modbus ASCII 协议
    }
}
