﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 流式解析器接口 (状态机核心) - 混合模式
    /// </summary>
    /// <typeparam name="T">解析出来的业务实体类型</typeparam>
    public interface IStreamParser<T>
    {
        /// <summary>
        /// 输入一个字节并尝试解析（适用于简单状态机）。
        /// </summary>
        /// <param name="b">输入的字节</param>
        /// <param name="result">解析成功时输出结果</param>
        /// <returns>是否解析成功</returns>
        bool TryParse(byte b, [NotNullWhen(true)] out T? result);

        /// <summary>
        /// 解析输入数据流（适用于高性能批量处理）。
        /// </summary>
        /// <param name="data">输入的数据块（使用 <see cref="ReadOnlySpan{T}"/> 高效传递）</param>
        /// <param name="output">用于存储解析结果的列表</param>
        /// <remarks>
        /// 默认实现：循环调用 TryParse。
        /// 如果你需要高性能优化（如 Modbus），请重写此方法。
        /// </remarks>
        void Parse(ReadOnlySpan<byte> data, List<T> output)
        {
            // 步骤1：逐字节调用 TryParse。
            // 为什么：默认实现需兼容状态机解析器按字节推进。
            // 风险点：若按块假设完整帧，遇到分片输入会解析失败。
            foreach (byte b in data)
            {
                // 步骤2：仅在解析成功时输出结果。
                // 为什么：保持输出集合只包含完整业务对象。
                // 风险点：误加半成品结果会污染上层处理逻辑。
                if (TryParse(b, out T? result))
                {
                    output.Add(result);
                }
            }
        }

        /// <summary>
        /// 重置解析状态机。
        /// </summary>
        void Reset();
    }
}
