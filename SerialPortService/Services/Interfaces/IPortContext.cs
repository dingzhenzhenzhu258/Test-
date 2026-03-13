﻿using System;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 串口上下文抽象。
    /// 统一定义串口生命周期与发送入口。
    /// </summary>
    public interface IPortContext : IDisposable
    {
        /// <summary>
        /// 串口名称（例如 COM3）。
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 打开串口并启动处理流水线。
        /// </summary>
        void Open();

        /// <summary>
        /// 关闭串口并停止处理流水线。
        /// </summary>
        void Close();

        /// <summary>
        /// 最近一次关闭流程是否完整成功。
        /// </summary>
        bool LastCloseSucceeded { get; }

        /// <summary>
        /// 发送原始数据。
        /// </summary>
        /// <param name="data">待发送数据</param>
        /// <returns>已发送数据（通常用于链路追踪）</returns>
        Task<byte[]> Send(byte[] data);

        /// <summary>
        /// 解析结果回调事件。
        /// </summary>
        event EventHandler<object>? OnHandleChanged;
    }
}
