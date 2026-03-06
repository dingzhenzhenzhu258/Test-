using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Models
{
    /// <summary>
    /// 数据包
    /// </summary>
    /// <param name="Data"></param>
    public record DataPacket(byte[] Data);
}
