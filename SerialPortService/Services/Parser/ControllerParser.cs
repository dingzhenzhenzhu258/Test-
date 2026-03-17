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
        /// <summary>
        /// 控制器定长帧缓冲区。
        /// </summary>
        private readonly byte[] _buffer = new byte[6];
        private int _index = 0;

        // 默认 Parse 方法会调用 TryParse，这里不需要重写 Parse

        /// <summary>
        /// 逐字节尝试解析控制器固定长度帧。
        /// </summary>
        public bool TryParse(byte b, [NotNullWhen(true)] out string? result)
        {
            // 步骤1：默认结果置空。
            // 为什么：仅在凑齐完整帧后输出业务结果。
            // 风险点：复用旧值会产生假阳性状态。
            result = null;

            // 步骤2：按固定 6 字节窗口累积。
            // 为什么：当前控制器协议为定长帧。
            // 风险点：无包头校验时遇到乱流可能错位解析。
            _buffer[_index++] = b;

            if (_index >= 6)
            {
                // 步骤3：凑满一帧后重置索引。
                // 为什么：准备接收下一帧数据。
                // 风险点：不重置会造成数组越界。
                _index = 0;

                // 步骤4：按业务位生成状态文本。
                // 为什么：将底层位值映射为上层可读语义。
                // 风险点：位定义变更未同步会导致状态反转。
                if (_buffer[3] == 0x01)
                    result = "按钮按下";
                else
                    result = "按钮未按下";

                return true;
            }
            return false;
        }

        /// <summary>
        /// 重置控制器解析状态。
        /// </summary>
        public void Reset()
        {
            // 步骤1：重置解析索引。
            // 为什么：在异常或复位场景恢复初始状态。
            // 风险点：索引残留会导致下一帧错位。
            _index = 0;
        }
    }
}
