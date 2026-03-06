using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace SerialPortService.Services.Parser
{
    public sealed class CustomFrame
    {
        public CustomFrame(byte command, byte[] payload, byte[] raw)
        {
            Command = command;
            Payload = payload;
            Raw = raw;
        }

        public byte Command { get; }
        public byte[] Payload { get; }
        public byte[] Raw { get; }
    }

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

        public bool TryParse(byte b, out CustomFrame? result)
        {
            result = null;

            switch (_state)
            {
                case State.WaitHeader:
                    if (b == Header)
                    {
                        Reset();
                        _raw.Add(b);
                        _state = State.ReadLength;
                    }
                    break;

                case State.ReadLength:
                    _length = b;
                    _raw.Add(b);
                    if (_length < 1 || _length > MaxLength)
                    {
                        Reset();
                        break;
                    }
                    _payloadRemaining = _length - 1;
                    _state = State.ReadCommand;
                    break;

                case State.ReadCommand:
                    _command = b;
                    _raw.Add(b);
                    _checksumCalc = (byte)(_length ^ _command);
                    _state = _payloadRemaining == 0 ? State.ReadChecksum : State.ReadPayload;
                    break;

                case State.ReadPayload:
                    _payload.Add(b);
                    _raw.Add(b);
                    _checksumCalc ^= b;
                    _payloadRemaining--;
                    if (_payloadRemaining == 0)
                    {
                        _state = State.ReadChecksum;
                    }
                    break;

                case State.ReadChecksum:
                    _checksum = b;
                    _raw.Add(b);
                    _state = State.ReadTail;
                    break;

                case State.ReadTail:
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
            _state = State.WaitHeader;
            _length = 0;
            _command = 0;
            _payloadRemaining = 0;
            _checksum = 0;
            _checksumCalc = 0;
            _payload.Clear();
            _raw.Clear();
        }
    }

    public static class CustomProtocolFrameBuilder
    {
        public static byte[] Build(byte command, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            var length = (byte)(1 + payload.Length);
            var checksum = (byte)(length ^ command);

            foreach (var b in payload)
            {
                checksum ^= b;
            }

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
