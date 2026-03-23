using Microsoft.Extensions.Logging;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Interfaces;
using System;
using System.IO.Ports;

namespace SerialPortService.Services
{
    /// <summary>
    /// 通用委托式上下文注册项。
    /// </summary>
    internal sealed class ProtocolContextRegistration : IPortContextRegistration
    {
        private readonly Func<HandleEnum, ProtocolEnum, bool> _predicate;
        private readonly Func<string, int, Parity, int, StopBits, HandleEnum, ProtocolEnum, ILoggerFactory, Handler.GenericHandlerOptions, IPortContext> _factory;

        public ProtocolContextRegistration(
            Func<HandleEnum, ProtocolEnum, bool> predicate,
            Func<string, int, Parity, int, StopBits, HandleEnum, ProtocolEnum, ILoggerFactory, Handler.GenericHandlerOptions, IPortContext> factory)
        {
            _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public bool CanHandle(HandleEnum handleEnum, ProtocolEnum protocol) => _predicate(handleEnum, protocol);

        public IPortContext Create(
            string portName,
            int baudRate,
            Parity parity,
            int dataBits,
            StopBits stopBits,
            HandleEnum handleEnum,
            ProtocolEnum protocol,
            ILoggerFactory loggerFactory,
            Handler.GenericHandlerOptions options)
            => _factory(portName, baudRate, parity, dataBits, stopBits, handleEnum, protocol, loggerFactory, options);
    }
}
