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
        // 固定缓冲区
        private readonly byte[] _buffer = new byte[512];
        private int _count = 0;

        // 功能码处理器字典
        private readonly Dictionary<byte, ModbusFunction> _functions;

        public ModbusRtuParser()
        {
            // 初始化支持的功能码
            // 实际项目中可以通过反射自动扫描，或者依赖注入
            _functions = new Dictionary<byte, ModbusFunction>();
            
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
            if (!_functions.ContainsKey(func.Code))
            {
                _functions.Add(func.Code, func);
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
            if (data.IsEmpty) return;
            for (int i = 0; i < data.Length; i++)
            {
                ParseByte(data[i], output);
            }
        }

        private void ParseByte(byte b, List<ModbusPacket> output)
        {
            // 1. 将字节放入缓冲区
            if (_count < _buffer.Length)
            {
                _buffer[_count++] = b;
            }
            else
            {
                Reset();
                _buffer[_count++] = b;
            }

            // 2. 状态机推进
            switch (_state)
            {
                case FrameParseState.WaitAddress:
                    _state = FrameParseState.WaitFuncCode;
                    break;

                case FrameParseState.WaitFuncCode:
                    _funcCode = b;
                    
                    // 检查是否为异常响应 (最高位为1)
                    if ((_funcCode & 0x80) != 0)
                    {
                        // 尝试查找 ErrorFunction (0x80)
                        if (_functions.TryGetValue(0x80, out var errFunc))
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
                        // 查找对应的功能码处理器
                        if (_functions.TryGetValue(_funcCode, out var func))
                        {
                            _currentFunction = func;

                            if (func.IsFixedLength)
                            {
                                _expectedDataLen = func.FixedDataLength;
                                _state = FrameParseState.WaitData;
                            }
                            else
                            {
                                // 变长数据：需要等待长度字节
                                // 我们使用特殊负数来标记状态吗？
                                // 不，我们可以直接利用 HeaderLength 和 LengthByteIndex
                                
                                // 逻辑：
                                // 我们需要知道什么时候能读到"长度字节"。
                                // 长度字节位于数据区起始位置 + LengthByteIndex。
                                // 当前 _count = 2 (Addr + Func)。
                                // 数据区还没开始。
                                
                                // 我们设置一个临时目标：等待读到 HeaderLength 那么长的数据
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
                    // 当前已收到的数据区长度 (总长度 - 2)
                    int dataLenSoFar = _count - 2;

                    if (_currentFunction != null && !_currentFunction.IsFixedLength && _expectedDataLen == -1)
                    {
                        // 正在等待读取头部以获取长度
                        // 我们需要检查是否已经收到了 LengthByteIndex 所指向的那个字节
                        
                        // HeaderLength 是指数据区头部的长度。
                        // 只要 dataLenSoFar > LengthByteIndex，我们就拿到了长度字节
                        
                        if (dataLenSoFar > _currentFunction.LengthByteIndex)
                        {
                            // 读取长度字节
                            // 注意：_buffer 的索引是 2 + LengthByteIndex
                            byte lengthVal = _buffer[2 + _currentFunction.LengthByteIndex];
                            
                            // 计算总期望数据长度 = HeaderLength + lengthVal
                            // 注意：有些协议 (如0x03) HeaderLength=1, Value=N. Total = 1+N.
                            // 有些协议 (如0x44) HeaderLength=5, Value=N. Total = 5+N.
                            // 所以公式是通用的。
                            _expectedDataLen = _currentFunction.HeaderLength + lengthVal;
                        }
                    }
                    
                    // 检查是否收够了数据
                    if (_expectedDataLen > 0 && dataLenSoFar >= _expectedDataLen)
                    {
                        _state = FrameParseState.WaitCRC;
                    }
                    break;

                case FrameParseState.WaitCRC:
                    int totalLen = 2 + _expectedDataLen + 2;
                    if (_count >= totalLen)
                    {
                        if (CheckCrc(_buffer.AsSpan(0, totalLen)))
                        {
                            var rawFrame = _buffer.AsSpan(0, totalLen).ToArray();
                            var packet = new ModbusPacket
                            {
                                SlaveId = _buffer[0],
                                FunctionCode = _buffer[1],
                                Data = _buffer.AsSpan(2, totalLen - 4).ToArray(),
                                RawFrame = rawFrame
                            };
                            output.Add(packet);
                        }
                        Reset();
                    }
                    break;
            }
        }

        public void Reset()
        {
            _count = 0;
            _state = FrameParseState.WaitAddress;
            _funcCode = 0;
            _expectedDataLen = 0;
            _currentFunction = null;
        }

        private bool CheckCrc(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 4) return false;
            byte receivedLo = frame[frame.Length - 2];
            byte receivedHi = frame[frame.Length - 1];
            ushort calcCrc = CalculateCrc(frame.Slice(0, frame.Length - 2));
            byte calcLo = (byte)(calcCrc & 0xFF);
            byte calcHi = (byte)(calcCrc >> 8);
            return receivedLo == calcLo && receivedHi == calcHi;
        }

        private ushort CalculateCrc(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    else
                        crc >>= 1;
                }
            }
            return crc;
        }
    }
}
