using AvailableVerificationAlgorithms.Crc;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SerialPortService.Helpers
{
    /// <summary>
    /// CRC 附加辅助类。
    /// </summary>
    public static class AddCrcHelpers
    {
        /// <summary>
        /// 在原始报文后面追加 CRC 校验码
        /// </summary>
        /// <param name="originalData"></param>
        /// <returns></returns>
        public static byte[] AddCRC(List<byte> originalData)
        {
            // 步骤1：直接通过 Span 计算 CRC，避免 List.ToArray() 额外分配。
            // 为什么：串口协议需要通过 CRC 校验保证数据完整性。
            // 风险点：CRC 算法或输入范围错误会导致设备拒收。
            var crc = Crc16Helpers.CalcCRC16(CollectionsMarshal.AsSpan(originalData));

            // 步骤2：将 CRC 追加到报文尾部。
            // 为什么：形成可直接发送的完整帧。
            // 风险点：追加字节顺序错误会导致校验失败。
            originalData.Add((byte)(crc & 0xFF));
            originalData.Add((byte)(crc >> 8));
            return originalData.ToArray();
        }
    }
}
