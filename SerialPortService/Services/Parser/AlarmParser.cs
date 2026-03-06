﻿using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace SerialPortService.Services.Parser
{
    /// <summary>
    /// 光电报警器解析器
    /// </summary>
    public class AlarmParser : IStreamParser<string>
    {
        private readonly Func<byte[]?> _getLastSentFunc; // 获取最后发送数据的委托

        // 协议固定长度 5: [FF] [LED] [Buzzer] [Flash] [AA]
        private readonly byte[] _buffer = new byte[5];
        private int _index = 0;

        public AlarmParser(Func<byte[]?> getLastSentFunc)
        {
            _getLastSentFunc = getLastSentFunc;
        }

        // 默认 Parse 方法会调用 TryParse，这里不需要重写 Parse
        
        public bool TryParse(byte b, [NotNullWhen(true)] out string? result)
        {
            result = null;

            // --- 状态机逻辑 ---

            // 1. 找包头 0xFF
            if (_index == 0)
            {
                if (b == 0xFF)
                {
                    _buffer[_index++] = b;
                }
                // 如果不是FF，忽略，继续等
                return false;
            }

            // 2. 接收中间数据 (索引 1, 2, 3)
            if (_index < 4)
            {
                _buffer[_index++] = b;
                return false;
            }

            // 3. 校验包尾 0xAA (索引 4)
            if (_index == 4)
            {
                if (b == 0xAA)
                {
                    _buffer[_index] = b; // 填入最后一个字节

                    // --- 解析成功！开始业务对比逻辑 (原 Handle 方法的逻辑) ---

                    // 拷贝一份当前收到的完整包
                    byte[] receivedData = _buffer.ToArray();

                    // 重置索引，准备接收下一个包
                    _index = 0;

                    // 获取最后发送的数据
                    byte[]? lastSent = _getLastSentFunc();

                    if (lastSent != null)
                    {
                        bool equal = lastSent.SequenceEqual(receivedData);

                        // 打印日志 (保留你原有的风格)
                        if (equal)
                        {
                            Console.WriteLine($"接收报文与发送报文一致 ✅");
                            result = "报文发送接收成功";
                        }
                        else
                        {
                            Console.WriteLine($"接收报文与发送报文不一致 ❌");
                            Console.WriteLine($"发送: {BitConverter.ToString(lastSent)}");
                            Console.WriteLine($"接收: {BitConverter.ToString(receivedData)}");
                            result = "报文发送接收失败";
                        }
                        return true; // 成功产出 result
                    }
                    else
                    {
                        Console.WriteLine($"没有可对比的发送报文");
                        result = "报文发送接收失败: _lastSent为null";
                        return true;
                    }
                }
                else
                {
                    // 包尾不对？可能是数据错位了
                    // 简单的容错处理：重置状态
                    // 进阶写法：如果 b 是 0xFF，可以直接设 _index=1，视作新包开始
                    _index = 0;
                    if (b == 0xFF) // 也许这个错误的尾巴其实是下一个包的头？
                    {
                        _buffer[_index++] = b;
                    }
                    return false;
                }
            }

            return false;
        }

        public void Reset()
        {
            _index = 0;
        }
    }
}
