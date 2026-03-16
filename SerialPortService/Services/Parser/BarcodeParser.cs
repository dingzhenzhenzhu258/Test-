﻿using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace SerialPortService.Services.Parser
{
    /// <summary>
    /// 扫码枪解析器
    /// </summary>
    public class BarcodeParser : IStreamParser<string>
    {
        // 步骤1：使用 StringBuilder 替代 string 拼接。
        // 为什么：逐字节 += string 在长条码场景下是 O(n²) 的分配开销。
        // 风险点：StringBuilder 需在输出/重置时手动清空。
        private readonly System.Text.StringBuilder _buffer = new();

        // 默认 Parse 方法会调用 TryParse，这里不需要重写 Parse

        public bool TryParse(byte b, out string result)
        {
            // 步骤2：将输入字节转为字符。
            // 为什么：扫码枪通常按 ASCII 文本流输出。
            // 风险点：编码不一致会导致字符解析异常。
            result = null;
            char c = (char)b;

            // 步骤3：遇到行结束符时输出完整扫码内容。
            // 为什么：多数扫码枪以 CR/LF 作为一条码结束标记。
            // 风险点：若终止符处理错误，会出现黏包或空包。
            if (c == '\r' || c == '\n')
            {
                if (_buffer.Length > 0)
                {
                    result = _buffer.ToString().Trim();
                    _buffer.Clear();
                    if (!string.IsNullOrEmpty(result))
                        return true;
                    return false;
                }

                return false;
            }
            else
            {
                // 步骤4：累积正文字符。
                // 为什么：等待完整终止符后再一次性输出。
                // 风险点：未累积将导致结果被截断。
                _buffer.Append(c);
                return false;
            }
        }

        public void Reset()
        {
            // 步骤1：清空内部缓存。
            // 为什么：为下一条扫码结果提供干净状态。
            // 风险点：缓存残留会导致新旧条码拼接。
            _buffer.Clear();
        }
    }
}
