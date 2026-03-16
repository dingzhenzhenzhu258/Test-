﻿using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Protocols.Modbus.Functions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace SerialPortService.Services.Protocols.Modbus
{
    /// <summary>
    /// Modbus解析器 - 实现RTU协议解析 (逐字节状态机版)
    /// </summary>
    public class ModbusRtuParser : IStreamParser<ModbusPacket>
    {
        private const int FrameAssembleGapMs = 50;

        // 固定缓冲区
        private readonly byte[] _buffer = new byte[512];
        private int _count = 0;
        private long _lastByteTickMs;

        // 功能码处理器数组（基于 O(1) 索引化，提升高频匹配性能）
        private readonly ModbusFunction?[] _functions;

        public ModbusRtuParser()
        {
            // 步骤1：初始化功能码处理器映射。
            // 为什么：不同功能码的数据长度策略不同，需要分发到对应规则。
            // 风险点：未注册功能码会导致报文被重置丢弃。
            _functions = new ModbusFunction?[256];
            
            Register(new ReadHoldingRegisters());
            Register(new WriteSingleRegister());
            Register(new WriteMultipleRegisters());
            Register(new ErrorFunction());
            
            // 注册自定义功能码
            Register(new CustomFunction44());
            Register(new CustomFunction42());
            Register(new CustomFunction45());
            Register(new CustomFunction46());
            Register(new CustomFunction50());
        }

        private void Register(ModbusFunction func)
        {
            // 步骤1：按功能码去重注册。
            // 为什么：保证解析规则唯一，避免同码多义。
            // 风险点：重复覆盖会导致长度规则混乱。
            if (_functions[func.Code] == null)
            {
                _functions[func.Code] = func;
            }
        }

        // 帧解析状态
        private enum FrameParseState
        {
            WaitAddress,    // 等待地址
            WaitFuncCode,   // 等待功能码
            WaitData,       // 等待数据区
            WaitCRC         // 等待CRC
        }

        private FrameParseState _state = FrameParseState.WaitAddress;
        private byte _funcCode;
        private int _expectedDataLen;
        private ModbusFunction? _currentFunction;

        /// <summary>
        /// 兼容旧的单字节解析方法
        /// </summary>
        public bool TryParse(byte b, [NotNullWhen(true)] out ModbusPacket? result)
        {
            // 步骤1：兼容单字节入口，转交批量解析主流程。
            // 为什么：统一解析路径可减少逻辑分叉。
            // 风险点：双路径维护容易产生行为不一致。
            result = null;
            Span<byte> singleByte = stackalloc byte[1] { b };
            var tempList = new List<ModbusPacket>(1);
            Parse(singleByte, tempList);

            if (tempList.Count > 0)
            {
                result = tempList[0];
                return true;
            }
            return false;
        }

        public void Parse(ReadOnlySpan<byte> data, List<ModbusPacket> output)
        {
            // 步骤1：逐字节驱动状态机。
            // 为什么：串口数据可能任意分片到达。
            // 风险点：按块假设完整帧会造成粘包/半包解析错误。
            if (data.IsEmpty) return;
            for (int i = 0; i < data.Length; i++)
            {
                ParseByte(data[i], output);
            }
        }

        private void ParseByte(byte b, List<ModbusPacket> output)
        {
            var nowTickMs = Environment.TickCount64;

            // 步骤1：检测帧内字节间隔是否异常 (即依靠超时重置半包死锁)。
            // 为什么：由于 Windows 线程调度精度有限, 此处时间戳检测只作为“防止无限死等”的次要辅助手段，主策略必须依赖“协议定义长度验证”。
            // 风险点：若依赖时间片定帧长则可靠性极低，当前结合 ModbusFunction 判定长度能够抵御极端的连续高波特率字节流。
            if (_state != FrameParseState.WaitAddress
                && _lastByteTickMs > 0
                && nowTickMs - _lastByteTickMs > FrameAssembleGapMs)
            {
                Reset();
            }

            _lastByteTickMs = nowTickMs;

            // 步骤1：写入接收缓冲区。
            // 为什么：状态机需要保留当前候选帧的原始字节。
            // 风险点：缓冲区溢出若不处理会触发越界异常。
            if (_count < _buffer.Length)
            {
                _buffer[_count++] = b;
            }
            else
            {
                // 步骤1.1：溢出时重置并以当前字节重新开始。
                // 为什么：尽快恢复同步，避免持续污染后续数据。
                // 风险点：极端噪声下会频繁重置导致丢帧。
                Reset();
                _buffer[_count++] = b;
            }

            // 步骤2：推进帧解析状态机。
            // 为什么：按地址、功能码、数据区、CRC 分阶段判定完整帧。
            // 风险点：状态切换条件错误会导致死等或误判。
            switch (_state)
            {
                case FrameParseState.WaitAddress:
                    // 步骤2.0：先校验站号范围。
                    // 为什么：Modbus 从站地址有效范围通常为 1~247，过滤噪声可减少假帧进入状态机。
                    // 风险点：若允许任意字节作为地址，噪声会放大错帧概率并触发后续超时。
                    if (b is < 1 or > 247)
                    {
                        Reset();
                        break;
                    }

                    _state = FrameParseState.WaitFuncCode;
                    break;

                case FrameParseState.WaitFuncCode:
                    _funcCode = b;
                    
                    // 步骤2.1：识别异常响应功能码（0x80 掩码）。
                    // 为什么：异常帧长度规则与正常帧不同。
                    // 风险点：误判异常位会导致长度计算错误。
                    if ((_funcCode & 0x80) != 0)
                    {
                        // 尝试查找 ErrorFunction (0x80)
                        var errFunc = _functions[0x80];
                        if (errFunc != null)
                        {
                            _currentFunction = errFunc;
                            _expectedDataLen = errFunc.FixedDataLength;
                            _state = FrameParseState.WaitData;
                        }
                        else
                        {
                            // 默认处理：错误码1字节
                            _expectedDataLen = 1;
                            _state = FrameParseState.WaitData;
                        }
                    }
                    else
                    {
                        // 步骤2.2：查找功能码处理器（O(1) 数组索引优化）。
                        // 为什么：每个功能码决定固定/变长解析策略，极限高频下消除 Hash 计算开销。
                        // 风险点：未知功能码若继续解析会污染状态机。
                        var func = _functions[_funcCode];
                        if (func != null)
                        {
                            _currentFunction = func;

                            if (func.IsFixedLength)
                            {
                                _expectedDataLen = func.FixedDataLength;
                                _state = FrameParseState.WaitData;
                            }
                            else
                            {
                                // 步骤2.3：变长帧进入“等待头部长度字节”模式。
                                // 为什么：只有读到长度字节后才能计算总数据区长度。
                                // 风险点：长度字节索引错误会导致截帧或越界。
                                _expectedDataLen = -1; // 标记为"正在等待头部"
                                _state = FrameParseState.WaitData;
                            }
                        }
                        else
                        {
                            // 未知功能码，重置
                            Reset();
                        }
                    }
                    break;

                case FrameParseState.WaitData:
                    // 步骤2.4：根据当前累计数据判断是否已收齐数据区。
                    // 为什么：到达数据区长度后才能进入 CRC 校验阶段。
                    // 风险点：提前进 CRC 会导致校验失败，滞后进 CRC 会等待超时。
                    int dataLenSoFar = _count - 2;

                    if (_currentFunction != null && !_currentFunction.IsFixedLength && _expectedDataLen == -1)
                    {
                        // 步骤2.4.1：检查长度字节是否到位。
                        // 为什么：变长帧总长度依赖该字段。
                        // 风险点：未到位就读取会得到错误长度。
                        if (dataLenSoFar > _currentFunction.LengthByteIndex)
                        {
                            // 步骤2.4.2：读取长度字节并计算期望数据区总长度。
                            // 为什么：为后续收包完成判定提供目标长度。
                            // 风险点：计算公式错误会导致永远收不齐或提前截断。
                            byte lengthVal = _buffer[2 + _currentFunction.LengthByteIndex];
                            
                            // 计算总期望数据长度 = HeaderLength + lengthVal
                            // 注意：有些协议 (如0x03) HeaderLength=1, Value=N. Total = 1+N.
                            // 有些协议 (如0x44) HeaderLength=5, Value=N. Total = 5+N.
                            // 所以公式是通用的。
                            _expectedDataLen = _currentFunction.HeaderLength + lengthVal;

                            // 步骤2.4.3：对变长数据区长度做上限保护。
                            // 为什么：噪声字节可能把长度字段污染成异常值，导致状态机长时间卡在等待数据。
                            // 风险点：无长度保护会放大偶发串口抖动，形成连续超时。
                            var maxDataLen = _buffer.Length - 4;
                            if (_expectedDataLen <= 0 || _expectedDataLen > maxDataLen)
                            {
                                Reset();
                            }
                        }
                    }
                    
                    // 检查是否收够了数据
                    if (_expectedDataLen > 0 && dataLenSoFar >= _expectedDataLen)
                    {
                        _state = FrameParseState.WaitCRC;
                    }
                    break;

                case FrameParseState.WaitCRC:
                    // 步骤2.5：收齐后执行 CRC 校验并产出包。
                    // 为什么：仅通过 CRC 的帧才可进入业务处理。
                    // 风险点：跳过 CRC 会把损坏数据交给上层。
                    int totalLen = 2 + _expectedDataLen + 2;
                    if (totalLen > _buffer.Length)
                    {
                        Reset();
                        break;
                    }

                    if (_count >= totalLen)
                    {
                        if (CheckCrc(_buffer.AsSpan(0, totalLen)))
                        {
                            var rawFrame = _buffer.AsSpan(0, totalLen).ToArray();
                            var packet = new ModbusPacket();
                            packet.SlaveId = _buffer[0];
                            packet.FunctionCode = _buffer[1];
                            packet.Data = _buffer.AsSpan(2, totalLen - 4).ToArray();
                            packet.RawFrame = rawFrame;

                            output.Add(packet);
                        }
                        Reset();
                    }
                    break;
            }
        }

        public void Reset()
        {
            // 步骤1：重置解析状态机。
            // 为什么：在出错或完成一帧后回到初始状态。
            // 风险点：状态残留会导致下一帧错位。
            _count = 0;
            _state = FrameParseState.WaitAddress;
            _funcCode = 0;
            _expectedDataLen = 0;
            _currentFunction = null;
        }

        private bool CheckCrc(ReadOnlySpan<byte> frame)
        {
            // 步骤1：读取报文尾部 CRC 并与计算值比对。
            // 为什么：验证链路传输完整性。
            // 风险点：CRC 不通过仍放行会引发业务误动作。
            if (frame.Length < 4) return false;
            byte receivedLo = frame[frame.Length - 2];
            byte receivedHi = frame[frame.Length - 1];
            ushort calcCrc = CalculateCrc(frame.Slice(0, frame.Length - 2));
            byte calcLo = (byte)(calcCrc & 0xFF);
            byte calcHi = (byte)(calcCrc >> 8);
            return receivedLo == calcLo && receivedHi == calcHi;
        }

        // 步骤1：预计算 Modbus CRC16 查表（多项式 0xA001）。
        // 为什么：查表法将每字节 8 次分支循环降为 1 次查表 + 异或，高频解析下 CPU 开销显著降低。
        // 风险点：表必须与逐位算法结果完全一致，否则所有帧校验失败。
        private static readonly ushort[] CrcTable = GenerateCrcTable();

        private static ushort[] GenerateCrcTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }
            return table;
        }

        private static ushort CalculateCrc(ReadOnlySpan<byte> data)
        {
            // 步骤1：执行 Modbus RTU 标准 CRC16 查表计算。
            // 为什么：与设备端算法保持一致，查表法高频下性能更优。
            // 风险点：实现偏差会导致所有帧校验失败。
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ data[i]) & 0xFF]);
            }
            return crc;
        }
    }
}
