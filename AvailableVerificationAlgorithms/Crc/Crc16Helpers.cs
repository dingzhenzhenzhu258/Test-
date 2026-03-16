using System;
using System.Collections.Generic;
using System.Text;

namespace AvailableVerificationAlgorithms.Crc
{
    public class Crc16Helpers
    {
        // 步骤1：预计算 Modbus CRC16 查表（多项式 0xA001）。
        // 为什么：查表法将每字节 8 次分支循环降为 1 次查表 + 异或，高频吞吐下 CPU 开销降低约 4-8 倍。
        // 风险点：表必须与逐位算法结果完全一致，否则所有帧校验失败。
        private static readonly ushort[] CrcTable = GenerateCrcTable();

        private static ushort[] GenerateCrcTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }
            return table;
        }

        /// <summary>
        /// 计算一段数据的 CRC16 校验码
        /// </summary>
        /// <param name="originalData"></param>
        /// <returns></returns>
        public static ushort CalcCRC16(byte[] originalData)
            => CalcCRC16(originalData.AsSpan());

        /// <summary>
        /// 计算一段数据的 CRC16 校验码（查表法，高频场景性能优于逐位计算）
        /// </summary>
        /// <param name="originalData">待计算的数据片段</param>
        /// <returns>CRC16</returns>
        public static ushort CalcCRC16(ReadOnlySpan<byte> originalData)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < originalData.Length; i++)
            {
                crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ originalData[i]) & 0xFF]);
            }
            return crc;
        }

        /// <summary>
        /// 计算 CRC16-CCITT-FALSE
        /// </summary>
        /// <param name="data">需要计算的字节数组</param>
        /// <param name="initialValue">初始值，默认为 0xFFFF。如果是连续计算，请传入上一次的结果</param>
        /// <returns>16位校验结果</returns>
        public static ushort Compute(byte[] data, ushort initialValue = 0xFFFF)
        {
            ushort crc = initialValue;
            const ushort polynomial = 0x1021; // 多项式 x16 + x12 + x5 + 1

            foreach (byte b in data)
            {
                // 将字节左移 8 位后与当前 CRC 高位异或
                crc ^= (ushort)(b << 8);

                for (int i = 0; i < 8; i++)
                {
                    // 检查最高位是否为 1
                    if ((crc & 0x8000) != 0)
                    {
                        crc = (ushort)((crc << 1) ^ polynomial);
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return (ushort)(crc & 0xFFFF);
        }

        /// <summary>
        /// 计算 CRC16-CCITT-FALSE
        /// </summary>
        /// <param name="data">需要计算的字节数组</param>
        /// <param name="initialValue">初始值，默认为 0xFFFF。如果是连续计算，请传入上一次的结果</param>
        /// <returns>16位校验结果</returns>
        public static ushort CRC16CCITTFALSECompute(byte[] data, ushort initialValue = 0xFFFF)
        {
            ushort crc = initialValue;
            const ushort polynomial = 0x1021; // 多项式 x16 + x12 + x5 + 1

            foreach (byte b in data)
            {
                // 将字节左移 8 位后与当前 CRC 高位异或
                crc ^= (ushort)(b << 8);

                for (int i = 0; i < 8; i++)
                {
                    // 检查最高位是否为 1
                    if ((crc & 0x8000) != 0)
                    {
                        crc = (ushort)((crc << 1) ^ polynomial);
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
            }

            return (ushort)(crc & 0xFFFF);
        }
    }
}
