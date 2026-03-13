namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 指标提供者接口。
    /// 每个处理器实例通过该接口暴露当前指标快照与标签维度。
    /// </summary>
    internal interface IGenericHandlerMetricsProvider
    {
        /// <summary>
        /// 处理器名称标签（例如 ModbusHandler）。
        /// </summary>
        string HandlerName { get; }

        /// <summary>
        /// 串口名称标签（例如 COM3）。
        /// </summary>
        string PortName { get; }

        /// <summary>
        /// 协议名称标签（例如 ModbusRTU）。
        /// </summary>
        string ProtocolName { get; }

        /// <summary>
        /// 设备类型标签（例如 TemperatureSensor）。
        /// </summary>
        string DeviceType { get; }

        /// <summary>
        /// 获取当前处理器指标快照。
        /// </summary>
        GenericHandlerMetrics GetMetrics();
    }
}
