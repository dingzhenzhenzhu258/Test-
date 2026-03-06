﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortService.Services.Interfaces
{
    /// <summary>
    /// 串口上下文接口
    /// </summary>
    public interface IPortContext : IDisposable
    {
        string Name { get; }
        void Open();
        void Close();
        Task<byte[]> Send(byte[] data);

        event EventHandler<object>? OnHandleChanged; // 或者 EventHandler<OperateResult<object>>
    }

    /// <summary>
    /// 泛型串口上下文接口 (支持强类型 SendRequestAsync)
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IPortContext<T> : IPortContext
    {
        // 可以在这里定义特定于 T 的方法，比如
        // Task<T> SendRequestAsync(byte[] command, int timeout = 1000);
        // 但由于 SendRequestAsync 的实现通常依赖于具体的协议逻辑（如 ModbusHandler），
        // 放在这里可能不通用。不过我们可以让 ModbusHandler 实现一个特定的接口。
    }
}
