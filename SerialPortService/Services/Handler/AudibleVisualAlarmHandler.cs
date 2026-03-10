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
            // 步骤1：将“最后发送报文访问器”注入解析器。
            // 为什么：解析器需要在收包时做发送-接收一致性比对。
            // 风险点：若无法访问最后发送报文，将失去一致性校验能力。
            _parser = new AlarmParser(() => _lastSent);
        }

        // 步骤1：向基类暴露解析器。
        // 为什么：基类读取流水线依赖该属性执行解析。
        // 风险点：返回错误解析器会导致报警器报文误判。
        protected override IStreamParser<string> Parser => _parser;

        // =================================================================
        // 静态工具方法 (保持不变)
        // =================================================================
        public static byte[] BuildCommand(LedMode led, BuzzerMode buzzer, FlashFrequency flash)
        {
            // 步骤1：按设备协议固定格式组帧。
            // 为什么：报警器命令必须满足头-参数-尾格式。
            // 风险点：字段顺序错误会导致设备拒收命令。
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