using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvailableVerificationAlgorithms.Crc
{
    public class Crc32Helpers
    {
        private static readonly uint[] StandardTable;
        private const uint Polynomial = 0x04C11DB7;

        static Crc32Helpers()
        {
            // 预计算标准 CRC32 查表 (Reflected)
            StandardTable = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((entry & 1) == 1)
                        entry = (entry >> 1) ^ 0xEDB88320;
                    else
                        entry >>= 1;
                }
                StandardTable[i] = entry;
            }
        }


        /// <summary>
        /// 标准 CRC-32 计算 (常用于网站、WinZip、以太网)
        /// 参数：输入反转=true, 输出反转=true, 初始值=0xFFFFFFFF, 结果异或=0xFFFFFFFF
        /// </summary>
        public static uint ComputeStandard(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                uint index = (crc ^ b) & 0xFF;
                crc = (crc >> 8) ^ StandardTable[index];
            }
            return crc ^ 0xFFFFFFFF;
        }


        /// <summary>
        /// STM32/LKS/LCM 硬件 CRC-32 计算 (默认模式)
        /// 参数：输入反转=false, 输出反转=false, 初始值=0xFFFFFFFF, 结果异或=0x00000000
        /// 特点：按 32位字 (Word) 计算
        public static uint ComputeHardwareCrc(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;

            // 1. 硬件要求按 4 字节对齐处理
            int length = bytes.Length;
            int wordCount = length / 4;

            // 2. 处理完整的 32 位字
            for (int i = 0; i < wordCount; i++)
            {
                // STM32 硬件读取小端字节流并拼成 uint32
                uint data = (uint)(bytes[i * 4] |
                                  (bytes[i * 4 + 1] << 8) |
                                  (bytes[i * 4 + 2] << 16) |
                                  (bytes[i * 4 + 3] << 24));

                crc ^= data;

                // 硬件内部的模2除法逻辑
                for (int j = 0; j < 32; j++)
                {
                    if ((crc & 0x80000000) != 0)
                        crc = (crc << 1) ^ Polynomial;
                    else
                        crc <<= 1;
                }
            }

            // 3. 注意：如果数据不是4的倍数，STM32 硬件的行为取决于具体的实现
            // 早期 F1/F4 忽略剩余字节，新款系列(G0/H7)可以配置按字节处理。
            // 这里默认返回 Word 计算后的结果（符合大多数硬件校验场景）

            return crc;
        }


        /// <summary>
        /// STM32/LKS/LCM 硬件 CRC-32 计算 (默认模式)
        /// 参数：输入反转=false, 输出反转=false, 初始值=0xFFFFFFFF, 结果异或=0x00000000
        /// 特点：按 32位字 (Word) 计算
        public static uint ComputeHardwareCrc(uint[] pbuf, uint size)
        {
            const uint init_value = 0x04C11DB7;
            uint crc_value = 0xFFFFFFFF;
            uint xbit;
            uint bits;

            for (uint i = 0; i < size; i++)
            {
                xbit = 0x80000000;
                for (bits = 0; bits < 32; bits++)
                {
                    if ((crc_value & 0x80000000) != 0)
                    {
                        crc_value <<= 1;
                        crc_value ^= init_value;
                    }
                    else
                    {
                        crc_value <<= 1;
                    }

                    if ((pbuf[i] & xbit) != 0)
                    {
                        crc_value ^= init_value;
                    }
                    xbit >>= 1;
                }
            }

            return crc_value;
        }
    }
}
