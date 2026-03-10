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
        private string _buffer = "";

        // 默认 Parse 方法会调用 TryParse，这里不需要重写 Parse

        public bool TryParse(byte b, out string result)
        {
            // 步骤1：将输入字节转为字符。
            // 为什么：扫码枪通常按 ASCII 文本流输出。
            // 风险点：编码不一致会导致字符解析异常。
            result = null;
            char c = (char)b;

            // 步骤2：遇到行结束符时输出完整扫码内容。
            // 为什么：多数扫码枪以 CR/LF 作为一条码结束标记。
            // 风险点：若终止符处理错误，会出现黏包或空包。
            if (c == '\r' || c == '\n')
            {
                if (!string.IsNullOrWhiteSpace(_buffer))
                {
                    result = _buffer.Trim();
                    _buffer = "";
                    return true;
                }

                // 步骤2.1：处理连续终止符。
                // 为什么：避免连续 CR/LF 产生空结果。
                // 风险点：不清空会把旧缓存带入下一帧。
                _buffer = "";
                return false;
            }
            else
            {
                // 步骤3：累积正文字符。
                // 为什么：等待完整终止符后再一次性输出。
                // 风险点：未累积将导致结果被截断。
                _buffer += c;
                return false;
            }
        }
        public void Reset()
        {
            // 步骤1：清空内部缓存。
            // 为什么：为下一条扫码结果提供干净状态。
            // 风险点：缓存残留会导致新旧条码拼接。
            _buffer = "";
        }
    }
}
