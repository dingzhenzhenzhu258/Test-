using Microsoft.Extensions.Logging;
using SerialPortService.Services.Interfaces;
using System;
using System.IO.Ports;

namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 基于固定解析器实例的通用串口上下文基类。
    /// </summary>
    /// <typeparam name="T">解析结果类型</typeparam>
    public abstract class ParserPortContext<T> : PortContext<T> where T : class
    {
        /// <summary>
        /// 固定绑定的流式解析器实例。
        /// </summary>
        private IStreamParser<T>? _parser;

        /// <summary>
        /// 创建仅初始化串口参数的解析上下文。
        /// </summary>
        protected ParserPortContext(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            ILogger logger,
            GenericHandlerOptions? options = null)
            : base(portName, baudRate, parity, dataBits, stopBits, logger, options)
        {
        }

        /// <summary>
        /// 创建并立即绑定解析器的串口上下文。
        /// </summary>
        protected ParserPortContext(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            IStreamParser<T> parser,
            ILogger logger,
            GenericHandlerOptions? options = null)
            : this(portName, baudRate, parity, dataBits, stopBits, logger, options)
        {
            SetParser(parser);
        }

        /// <summary>
        /// 绑定解析器实例。
        /// </summary>
        /// <param name="parser">流式解析器</param>
        protected void SetParser(IStreamParser<T> parser)
        {
            // 步骤1：注入并保存固定解析器实例。
            // 为什么：减少子类重复定义 Parser 字段与属性。
            // 风险点：若传入空解析器会导致读取链路不可用。
            if (_parser != null)
            {
                throw new InvalidOperationException("Parser has already been configured.");
            }

            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        }

        /// <summary>
        /// 获取当前已绑定的解析器实例。
        /// </summary>
        protected sealed override IStreamParser<T> Parser
            => _parser ?? throw new InvalidOperationException("Parser has not been configured.");
    }
}
