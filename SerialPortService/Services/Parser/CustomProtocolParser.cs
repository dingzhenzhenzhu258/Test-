using SerialPortService.Models;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace SerialPortService.Services.Parser
{
    /// <summary>
    /// 自定义协议状态机解析器。
    /// 帧格式：AA | Length | Command | Payload | Checksum | 55。
    /// </summary>
    public sealed class CustomProtocolParser : IStreamParser<CustomFrame>
    {
        private const byte Header = 0xAA;
        private const byte Tail = 0x55;
        private const int MaxLength = 64;

        private enum State
        {
            WaitHeader,
            ReadLength,
            ReadCommand,
            ReadPayload,
            ReadChecksum,
            ReadTail
        }

        private State _state = State.WaitHeader;
        private byte _length;
        private byte _command;
        private int _payloadRemaining;
        private byte _checksum;
        private byte _checksumCalc;
        private readonly List<byte> _payload = new();
        private readonly List<byte> _raw = new();

        public bool TryParse(byte b, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out CustomFrame result)
        {
            // 步骤1：默认置空解析结果。
            // 为什么：只有完整帧通过校验才允许输出对象。
            // 风险点：若复用旧 result，可能把上一次解析结果误返回。
            result = null!;

            // 步骤2：按状态机消费字节流。
            // 为什么：串口数据天然分片，需要逐字节累积协议上下文。
            // 风险点：状态跳转错误会导致帧错位或永久不同步。
            switch (_state)
            {
                case State.WaitHeader:
                    // 步骤2.1：等待帧头。
                    // 为什么：从随机字节流中重新对齐协议边界。
                    // 风险点：若不校验帧头，后续长度字段将不可置信。
                    if (b == Header) { Reset(); _raw.Add(b); _state = State.ReadLength; }
                    break;

                case State.ReadLength:
                    // 步骤2.2：读取并校验长度字段。
                    // 为什么：长度决定后续命令/负载读取窗口。
                    // 风险点：长度越界会导致内存扩张或状态机失控。
                    _length = b;
                    _raw.Add(b);
                    if (_length < 1 || _length > MaxLength) { Reset(); break; }
                    _payloadRemaining = _length - 1;
                    _state = State.ReadCommand;
                    break;

                case State.ReadCommand:
                    // 步骤2.3：读取命令字并初始化校验和。
                    // 为什么：命令字参与 checksum 计算。
                    // 风险点：校验初值错误会导致整帧误判失败。
                    _command = b;
                    _raw.Add(b);
                    _checksumCalc = (byte)(_length ^ _command);
                    _state = _payloadRemaining == 0 ? State.ReadChecksum : State.ReadPayload;
                    break;

                case State.ReadPayload:
                    // 步骤2.4：逐字节累计负载并更新校验和。
                    // 为什么：支持任意分片到达的负载流。
                    // 风险点：负载计数不一致会造成后续字段错位。
                    _payload.Add(b); _raw.Add(b); _checksumCalc ^= b; _payloadRemaining--;
                    if (_payloadRemaining == 0) _state = State.ReadChecksum;
                    break;

                case State.ReadChecksum:
                    // 步骤2.5：读取校验字段。
                    // 为什么：下一个状态需进行完整帧有效性判定。
                    // 风险点：若跳过校验字段，错误数据会被当成合法帧。
                    _checksum = b; _raw.Add(b); _state = State.ReadTail;
                    break;

                case State.ReadTail:
                    // 步骤2.6：读取帧尾并执行最终校验。
                    // 为什么：帧尾+checksum 双条件可提高抗干扰能力。
                    // 风险点：不做最终校验会输出损坏帧。
                    _raw.Add(b);
                    if (b == Tail && _checksum == _checksumCalc)
                    {
                        result = new CustomFrame(_command, _payload.ToArray(), _raw.ToArray());
                        Reset();
                        return true;
                    }
                    Reset();
                    break;
            }

            return false;
        }

        public void Reset()
        {
            // 步骤1：恢复状态机到初始状态。
            // 为什么：为下一帧解析提供干净上下文。
            // 风险点：残留状态会污染下一次解析。
            _state = State.WaitHeader;
            _length = 0; _command = 0; _payloadRemaining = 0; _checksum = 0; _checksumCalc = 0;
            _payload.Clear();
            _raw.Clear();
        }
    }

    /// <summary>
    /// 自定义协议帧构建器。
    /// </summary>
    public static class CustomProtocolFrameBuilder
    {
        /// <summary>
        /// 构建一帧可发送的自定义协议报文。
        /// </summary>
        public static byte[] Build(byte command, byte[] payload)
        {
            // 步骤1：标准化可空负载。
            // 为什么：统一后续长度与校验计算逻辑。
            // 风险点：空负载未处理会触发空引用异常。
            payload ??= Array.Empty<byte>();

            // 步骤2：计算长度与校验和。
            // 为什么：确保接收侧可按协议正确校验。
            // 风险点：长度/校验错误会导致对端丢帧。
            var length = (byte)(1 + payload.Length);
            var checksum = (byte)(length ^ command);
            foreach (var b in payload) checksum ^= b;

            // 步骤3：按协议格式组帧。
            // 为什么：保持发送报文与解析状态机一致。
            // 风险点：字段顺序错误会导致接收端无法识别。
            var data = new byte[5 + payload.Length];
            data[0] = 0xAA;
            data[1] = length;
            data[2] = command;
            Array.Copy(payload, 0, data, 3, payload.Length);
            data[^2] = checksum;
            data[^1] = 0x55;
            return data;
        }
    }
}
