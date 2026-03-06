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
            result = null;
            char c = (char)b;

            // 遇到回车或换行，认为扫码结束
            if (c == '\r' || c == '\n')
            {
                if (!string.IsNullOrWhiteSpace(_buffer))
                {
                    result = _buffer.Trim();
                    _buffer = "";
                    return true;
                }
                // 如果是空的（比如连续的\r\n），清空但不返回
                _buffer = "";
                return false;
            }
            else
            {
                _buffer += c;
                return false;
            }
        }
        public void Reset() => _buffer = "";
    }
}
