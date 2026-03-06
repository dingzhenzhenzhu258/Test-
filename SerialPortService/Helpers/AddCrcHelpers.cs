using AvailableVerificationAlgorithms.Crc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Helpers
{
    public class AddCrcHelpers
    {
        #region CRC

        /// <summary>
        /// 在原始报文后面追加 CRC 校验码
        /// </summary>
        /// <param name="originalData"></param>
        /// <returns></returns>
        public static byte[] AddCRC(List<byte> originalData)
        {
            var crc = Crc16Helpers.CalcCRC16(originalData.ToArray());
            var crcBytes = BitConverter.GetBytes(crc);
            originalData.AddRange(crcBytes);
            return originalData.ToArray();
        }
        #endregion
    }
}
