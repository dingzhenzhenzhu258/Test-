using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvailableVerificationAlgorithms.Aligning
{
    public class SixteenBytes
    {
        /// <summary>
        /// 将原始字节列表对齐至16字节倍数，并转换为uint数组
        /// </summary>
        /// <param name="hexValues">原始16进制字符串列表</param>
        /// <param name="paddingValue">填充值，通常为 0x00 或 0xFF</param>
        /// <returns>对齐后的 uint 数组</returns>
        public static uint[] AlignAndConvertToUint(List<string> hexValues, byte paddingValue = 0xFF)
        {
            if (hexValues == null) return new uint[0];

            // 1. 计算对齐后的字节总数 (向上取16的倍数)
            int originalCount = hexValues.Count;
            int alignedByteCount = ((originalCount + 15) / 16) * 16;

            // 2. 每个 uint 占 4 字节，计算 uint 数组长度
            int uintCount = alignedByteCount / 4;
            uint[] pbuf = new uint[uintCount];

            // 3. 填充 uint 数组
            for (int i = 0; i < uintCount; i++)
            {
                // 依次获取 4 个字节，如果索引超出原始列表，则使用 paddingValue
                byte[] bytes = new byte[4];
                for (int j = 0; j < 4; j++)
                {
                    int currentIndex = i * 4 + j;
                    if (currentIndex < originalCount)
                    {
                        // 转换失败时默认为 0
                        if (!byte.TryParse(hexValues[currentIndex], System.Globalization.NumberStyles.HexNumber, null, out bytes[j]))
                        {
                            bytes[j] = 0;
                        }
                    }
                    else
                    {
                        bytes[j] = paddingValue; // 进行 16 字节对齐填充
                    }
                }

                // 4. 小端序拼接 (Little-Endian)
                // 字节流: [0, 1, 2, 3] -> uint: 0x03020100
                pbuf[i] = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
            }

            return pbuf;
        }
    }
}
