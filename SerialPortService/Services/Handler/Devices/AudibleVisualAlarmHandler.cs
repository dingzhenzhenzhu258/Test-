using SerialPortService.Models.Enums;
using SerialPortService.Services.Parser;
using Microsoft.Extensions.Logging;
using System.IO.Ports;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 光电报警处理器
    /// </summary>
    public class AudibleVisualAlarmHandler : ParserPortContext<string>
    {
        public AudibleVisualAlarmHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger)
            : base(portName, baudRate, parity, dataBits, stopBits, logger)
        {
            // 步骤1：将“最后发送报文访问器”注入解析器。
            // 为什么：解析器需要在收包时做发送-接收一致性比对。
            // 风险点：若无法访问最后发送报文，将失去一致性校验能力。
            SetParser(new AlarmParser(() => _lastSent));
        }

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
}
