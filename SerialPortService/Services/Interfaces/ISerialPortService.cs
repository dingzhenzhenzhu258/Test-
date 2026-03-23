using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using SerialPortService.Models.Enums;
using SerialPortService.Services.Protocols;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 串口服务统一入口接口。
    /// </summary>
    public interface ISerialPortService
    {
        OperateResult OpenPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol = ProtocolEnum.Default);

        OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class;

        OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory) where T : class;

        Task<OperateResult> OpenPortAsync(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol = ProtocolEnum.Default);

        Task<OperateResult> OpenPortAsync<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class;

        Task<OperateResult> OpenPortAsync<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Func<IStreamParser<T>> parserFactory) where T : class;

        Task<OperateResult> ClosePortAsync(string portName);

        OperateResult CloseAll();

        Task<OperateResult> CloseAllAsync();

        bool TryGetContext(string portName, out IPortContext? context);

        ContextRegistrationResult RegisterContextRegistration(string key, IPortContextRegistration registration);

        ParserRegistrationResult RegisterParser<T>(ProtocolEnum protocol, string key, Func<IStreamParser<T>> factory) where T : class;

        ProtocolDefinitionRegistrationResult RegisterProtocolDefinition<TPacket>(string key, IProtocolDefinition<TPacket> definition) where TPacket : class;

        bool TryGetProtocolDefinition<TPacket>(ProtocolEnum protocol, out IProtocolDefinition<TPacket>? definition) where TPacket : class;

        void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver);

        void RefreshPorts(ref List<string> oldPortNames);

        bool IsOpen(string portName);

        ServiceHealthSnapshot GetHealthSnapshot();

        ServiceDiagnosticReport GetDiagnosticReport(int maxItems = 20);

        PortRuntimeSnapshotResult GetPortRuntimeSnapshot(string portName);

        Task<OperateResult> RestartPortAsync(string portName);

        Task<BatchPortOperationResult> RestartPortsAsync(IEnumerable<string> portNames);

        Task<byte[]> Write(string portName, byte[] data);

        Task<OperateResult<byte[]>> TryWrite(string portName, byte[] data);
    }
}
