﻿using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SerialPortService.Services.Parser
{
    /// <summary>
    /// 控制器解析器
    /// </summary>
    public class ControllerParser : IStreamParser<string>
    {
        private readonly byte[] _buffer = new byte[6];
        private int _index = 0;

        // 默认 Parse 方法会调用 TryParse，这里不需要重写 Parse

        public bool TryParse(byte b, [NotNullWhen(true)] out string? result)
        {
            result = null;

            // 简单逻辑：填满6个字节就算一个包
            // (实际建议加包头判断，防止错位)
            _buffer[_index++] = b;

            if (_index >= 6)
            {
                _index = 0; // 重置

                // 这里照搬你原来的逻辑
                if (_buffer[3] == 0x01)
                    result = "按钮按下";
                else
                    result = "按钮未按下";

                return true;
            }
            return false;
        }

        public void Reset() => _index = 0;
    }
}
