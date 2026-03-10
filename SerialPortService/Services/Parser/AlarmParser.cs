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
            // 步骤1：默认结果置空。
            // 为什么：仅完整且校验通过的报文才输出结果。
            // 风险点：结果残留会导致状态误判。
            result = null;

            // 步骤2：查找包头 0xFF。
            // 为什么：从串流中定位一帧起始边界。
            // 风险点：包头判断错误会造成整帧错位。
            if (_index == 0)
            {
                if (b == 0xFF)
                {
                    _buffer[_index++] = b;
                }
                return false;
            }

            // 步骤3：接收中间数据区。
            // 为什么：固定 5 字节协议需依次填充 LED/Buzzer/Flash。
            // 风险点：中间区丢字节会导致尾校验无效。
            if (_index < 4)
            {
                _buffer[_index++] = b;
                return false;
            }

            // 步骤4：校验包尾 0xAA。
            // 为什么：确认报文完整性。
            // 风险点：尾字节错误应及时重置状态避免污染后续解析。
            if (_index == 4)
            {
                if (b == 0xAA)
                {
                    _buffer[_index] = b; // 填入最后一个字节

                    // 步骤5：拷贝完整接收包并重置索引。
                    // 为什么：后续对比和下一个包接收都依赖干净状态。
                    // 风险点：不拷贝直接复用缓冲区会被后续写入覆盖。
                    byte[] receivedData = _buffer.ToArray();
                    _index = 0;

                    // 步骤6：获取最后发送报文并执行一致性对比。
                    // 为什么：报警器回包应与发送命令一致。
                    // 风险点：未对比会放过错误回包或串口串台。
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
                    // 步骤7：包尾错误时执行容错重置。
                    // 为什么：快速恢复状态机，等待下一帧起点。
                    // 风险点：不重置会让解析持续错位。
                    _index = 0;
                    if (b == 0xFF)
                    {
                        // 步骤7.1：若当前字节可能是新包头，立即复用。
                        // 为什么：减少错位后的恢复时间。
                        // 风险点：误判包头会再次进入错误状态。
                        _buffer[_index++] = b;
                    }
                    return false;
                }
            }

            return false;
        }

        public void Reset()
        {
            // 步骤1：重置索引。
            // 为什么：恢复解析器初始状态。
            // 风险点：索引残留会导致下一帧错位。
            _index = 0;
        }
    }
}
