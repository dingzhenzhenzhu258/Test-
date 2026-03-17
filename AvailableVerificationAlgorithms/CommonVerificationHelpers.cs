using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AvailableVerificationAlgorithms
{
    /// <summary>
    /// 常见校验与摘要算法工具类。
    /// 提供 LRC、SUM、XOR、SHA256、MD5 等常用方法。
    /// </summary>
    public static class CommonVerificationHelpers
    {
        // 步骤1：LRC 校验（Longitudinal Redundancy Check）
        // 为什么：常用于串口通信帧尾校验，能快速检测单字节错误。
        // 风险点：无法检测多字节错位或重排。
        public static byte CalcLrc(byte[] data)
        {
            byte lrc = 0;
            foreach (var b in data)
                lrc += b;
            return (byte)(-lrc);
        }

        // 步骤2：SUM 校验（简单累加）
        // 为什么：适用于低成本场景，能检测单字节错误。
        // 风险点：无法检测顺序错位。
        public static byte CalcSum(byte[] data)
        {
            byte sum = 0;
            foreach (var b in data)
                sum += b;
            return sum;
        }

        // 步骤3：XOR 校验（异或累加）
        // 为什么：能检测单字节错误和部分顺序错位。
        // 风险点：无法检测所有多字节错位。
        public static byte CalcXor(byte[] data)
        {
            byte xor = 0;
            foreach (var b in data)
                xor ^= b;
            return xor;
        }

        // 步骤4：SHA256 摘要
        // 为什么：用于数据完整性校验，安全性高。
        // 风险点：性能开销较大，不适合高频实时场景。
        public static string CalcSha256(byte[] data)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public static string CalcSha256(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return CalcSha256(bytes);
        }

        public static string CalcSha256(Stream stream)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        // 步骤5：MD5 摘要
        // 为什么：用于文件校验、低安全场景。
        // 风险点：已不适合安全敏感场景。
        public static string CalcMd5(byte[] data)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public static string CalcMd5(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return CalcMd5(bytes);
        }

        public static string CalcMd5(Stream stream)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
