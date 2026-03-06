using SerialPortService.Services.Interfaces;
using SerialPortService.Services.Parser;
using Microsoft.Extensions.Logging;
using System;
using System.IO.Ports;
using System.Linq;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 光电报警处理器
    /// </summary>
    public class AudibleVisualAlarmHandler : PortContext<string>
    {
        // 1. 实例化我们自定义的解析器
        private readonly AlarmParser _parser;

        public AudibleVisualAlarmHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
            // 将获取 _lastSent 的能力传给解析器
            // 这样解析器就能在底层直接进行"发送 vs 接收"的对比
            _parser = new AlarmParser(() => _lastSent);
        }

        // 2. 实现基类的抽象属性，提供解析器
        protected override IStreamParser<string> Parser => _parser;

        // =================================================================
        // 静态工具方法 (保持不变)
        // =================================================================
        public static byte[] BuildCommand(LedMode led, BuzzerMode buzzer, FlashFrequency flash)
        {
            return new byte[]
            {
                0xFF,               // 命令头
                (byte)led,          // LED模式
                (byte)buzzer,       // 蜂鸣器模式
                (byte)flash,        // 闪光频率
                0xAA                // 命令尾
            };
        }
    }

    // --- 枚举保持不变 ---
    public enum LedMode : byte
    {
        Off = 0x01, Green = 0x02, Yellow = 0x03, Red = 0x04
    }

    public enum BuzzerMode : byte
    {
        Off = 0x01, On = 0x02
    }

    public enum FlashFrequency : byte
    {
        Flash_off = 0x01, Flash_085s = 0x02, Flash_17s = 0x03, Flash_25s = 0x04
    }
}