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
        /// <summary>
        /// 创建声光报警器处理器。
        /// </summary>
        public AudibleVisualAlarmHandler(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, ILogger logger, GenericHandlerOptions? options = null)
            : base(portName, baudRate, parity, dataBits, stopBits, logger, options)
        {
            // 步骤1：将“最后发送报文访问器”注入解析器。
            // 为什么：解析器需要在收包时做发送-接收一致性比对。
            // 风险点：若无法访问最后发送报文，将失去一致性校验能力。
            SetParser(new AlarmParser(() => _lastSent));
        }

        /// <summary>
        /// 按协议生成报警器控制命令。
        /// </summary>
        /// <param name="led">LED 模式</param>
        /// <param name="buzzer">蜂鸣器模式</param>
        /// <param name="flash">闪光频率</param>
        /// <returns>完整 5 字节命令帧</returns>
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
