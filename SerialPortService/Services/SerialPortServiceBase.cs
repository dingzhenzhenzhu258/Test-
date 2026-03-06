using SerialPortService.Models;
using SerialPortService.Models.Emuns;
using SerialPortService.Services.Handler;
using SerialPortService.Services.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SerialPortService.Services
{
    /// <summary>
    /// 串口服务基类。
    /// 负责串口上下文创建、协议路由、生命周期管理与外部调用入口封装。
    /// </summary>
    /// <remarks>
    /// 该类面向业务层提供统一 API（Open/Close/Write/TryGetContext），
    /// 并通过 <see cref="GenericHandlerOptions"/> 统一下发并发/限流/重连策略。
    /// </remarks>
    public class SerialPortServiceBase : ISerialPortService
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly GenericHandlerOptions _genericHandlerOptions;
        private static readonly ConcurrentDictionary<HandleEnum, ISerialPortService.PortContextFactory> handlerFactories = new();
        private static Func<HandleEnum, ProtocolEnum>? protocolResolver;

        public SerialPortServiceBase(ILoggerFactory? loggerFactory = null, GenericHandlerOptions? genericHandlerOptions = null)
        {
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _genericHandlerOptions = genericHandlerOptions ?? new GenericHandlerOptions();
            SerialPortReconnectPolicy.Configure(_genericHandlerOptions.ReconnectIntervalMs, _genericHandlerOptions.MaxReconnectAttempts);
        }

        private GenericHandlerOptions CreateTaggedOptions(HandleEnum handleEnum, ProtocolEnum protocol)
        {
            return new GenericHandlerOptions
            {
                ResponseChannelCapacity = _genericHandlerOptions.ResponseChannelCapacity,
                SampleLogInterval = _genericHandlerOptions.SampleLogInterval,
                DropWhenNoActiveRequest = _genericHandlerOptions.DropWhenNoActiveRequest,
                ResponseChannelFullMode = _genericHandlerOptions.ResponseChannelFullMode,
                ProtocolTag = protocol.ToString(),
                DeviceTypeTag = handleEnum.ToString(),
                ReconnectIntervalMs = _genericHandlerOptions.ReconnectIntervalMs,
                MaxReconnectAttempts = _genericHandlerOptions.MaxReconnectAttempts
            };
        }

        /// <summary>
        /// 串口上下文集合 key: 串口号 value: 串口上下文
        /// </summary>
        private static readonly ConcurrentDictionary<string, IPortContext> ports = new();

        // 对外只暴露 IReadOnlyDictionary 接口
        public static IReadOnlyDictionary<string, IPortContext> OnlyReadports => ports;

        private (IPortContext Context, ProtocolEnum ResolvedProtocol) CreateContext(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum, ProtocolEnum protocol)
        {
            var resolvedProtocol = protocol;

            if (resolvedProtocol == ProtocolEnum.Default)
            {
                if (protocolResolver != null)
                {
                    resolvedProtocol = protocolResolver(handleEnum);
                }
                else
                {
                    switch (handleEnum)
                    {
                        case HandleEnum.TemperatureSensor:
                        case HandleEnum.ServoMotor:
                            resolvedProtocol = ProtocolEnum.ModbusRTU;
                            break;
                    }
                }
            }

            if (handlerFactories.TryGetValue(handleEnum, out var factory))
            {
                return (factory(portName, baudRate, parity, dataBits, stopBits, handleEnum, resolvedProtocol, _loggerFactory), resolvedProtocol);
            }

            IPortContext context = handleEnum switch
            {
                HandleEnum.AudibleVisualAlarmHandler => new AudibleVisualAlarmHandler(portName, baudRate, parity, dataBits, stopBits, _loggerFactory.CreateLogger<AudibleVisualAlarmHandler>()),
                HandleEnum.BarcodeScanner => new BarcodeScannerHandler(portName, baudRate, parity, dataBits, stopBits, _loggerFactory.CreateLogger<BarcodeScannerHandler>()),
                HandleEnum.TemperatureSensor => new TemperatureSensorHandler(portName, baudRate, parity, dataBits, stopBits, ParserFactory.CreateModbusParser(resolvedProtocol), _loggerFactory.CreateLogger<TemperatureSensorHandler>()),
                HandleEnum.Default => resolvedProtocol == ProtocolEnum.ModbusRTU || resolvedProtocol == ProtocolEnum.ModbusASCII
                    ? new ModbusHandler(portName, baudRate, parity, dataBits, stopBits, _loggerFactory.CreateLogger<ModbusHandler>(), CreateTaggedOptions(handleEnum, resolvedProtocol))
                    : throw new InvalidOperationException("未指定设备类型，且协议不支持自动推断 Handler"),
                _ => throw new ArgumentOutOfRangeException(nameof(handleEnum))
            };

            return (context, resolvedProtocol);
        }

        /// <summary>
        /// 打开串口 (使用预定义设备枚举)
        /// 如果 handleEnum 为 Default，则必须指定 protocol
        /// </summary>
        public OperateResult OpenPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, HandleEnum handleEnum = HandleEnum.Default, ProtocolEnum protocol = ProtocolEnum.Default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                var resolvedProtocol = protocol;
                var context = ports.GetOrAdd(portName, _ =>
                {
                    var result = CreateContext(portName, baudRate, parity, dataBits, stopBits, handleEnum, protocol);
                    resolvedProtocol = result.ResolvedProtocol;
                    return result.Context;
                });

                context.Open();

                string info = $"串口 {portName} 打开成功，模式：{handleEnum} / {resolvedProtocol}，参数：波特率={baudRate}, 数据位={dataBits}, 校验位={parity}, 停止位={stopBits}";
                return new OperateResult(true, info, 0);
            }
            catch (Exception e)
            {
                return new OperateResult(false, $"串口打开失败：{e.Message}", -1);
            }
        }

        /// <summary>
        /// 打开串口 (使用自定义解析器)
        /// </summary>
        public OperateResult OpenPort<T>(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, IStreamParser<T> parser) where T : class
        {
            try
            {
                if (string.IsNullOrWhiteSpace(portName))
                    return new OperateResult(false, "串口名不能为空", -1);

                var context = ports.GetOrAdd(portName, _ => new GenericHandler<T>(portName, baudRate, parity, dataBits, stopBits, parser, _loggerFactory.CreateLogger<GenericHandler<T>>(), CreateTaggedOptions(HandleEnum.Default, ProtocolEnum.Default)));
                context.Open();

                string info = $"串口 {portName} 打开成功 (自定义解析器)，参数：波特率={baudRate}, 数据位={dataBits}, 校验位={parity}, 停止位={stopBits}";
                return new OperateResult(true, info, 0);
            }
            catch (Exception e)
            {
                return new OperateResult(false, $"串口打开失败：{e.Message}", -1);
            }
        }

        /// <summary>
        /// 写对应串口数据
        /// </summary>
        /// <param name="portName"></param>
        /// <param name="data"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public async Task<byte[]> Write(string portName, byte[] data)
        {
            if (ports.TryGetValue(portName, out var context))
            {
                return await context.Send(data).ConfigureAwait(false);

            }
            else
                throw new InvalidOperationException($"串口 {portName} 未打开");
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        /// <param name="portName"></param>
        /// <returns></returns>
        public OperateResult ClosePort(string portName)
        {
            if (!ports.TryRemove(portName, out var context))
                return new OperateResult(false, $"未找到串口 {portName}", -1);

            context.Close();
            context.Dispose();

            return new OperateResult(true, $"{portName} 关闭成功", 0);
        }

        public OperateResult CloseAll()
        {
            var portNames = ports.Keys.ToList();
            var errors = new List<string>();

            foreach (var portName in portNames)
            {
                if (!ports.TryRemove(portName, out var context))
                    continue;

                try
                {
                    context.Close();
                    context.Dispose();
                }
                catch (Exception ex)
                {
                    errors.Add($"{portName}:{ex.Message}");
                }
            }

            if (errors.Count > 0)
                return new OperateResult(false, $"部分关闭失败: {string.Join("; ", errors)}", -1);

            return new OperateResult(true, $"关闭完成: {portNames.Count}", 0);
        }

        public bool TryGetContext(string portName, out IPortContext? context)
        {
            return ports.TryGetValue(portName, out context);
        }

        public bool RegisterHandlerFactory(HandleEnum handleEnum, ISerialPortService.PortContextFactory factory)
        {
            return handlerFactories.TryAdd(handleEnum, factory);
        }

        public void SetProtocolResolver(Func<HandleEnum, ProtocolEnum> resolver)
        {
            protocolResolver = resolver;
        }

        /// <summary>
        /// 检查对应串口是否打开
        /// </summary>
        /// <param name="portName"></param>
        /// <returns></returns>
        public bool IsOpen(string portName) => ports.ContainsKey(portName);

        /// <summary>
        /// 刷新串口
        /// </summary>
        /// <param name="oldPortNames"></param>
        public void RefreshPorts(ref List<string> oldPortNames)
        {
            var current = SerialPort.GetPortNames();

            oldPortNames = oldPortNames.Intersect(current).ToList();
            oldPortNames.AddRange(current.Except(oldPortNames));
        }
    }
}
