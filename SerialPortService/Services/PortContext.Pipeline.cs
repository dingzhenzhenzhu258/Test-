using SerialPortService.Models;
using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SerialPortService.Services
{
    public abstract partial class PortContext<T>
    {
        /// <summary>
        /// Stage 1: IO 异步读取任务 (Producer)
        /// </summary>
        private async Task IoReadLoopAsync(CancellationToken token)
        {
            var readSize = RuntimeOptions.RawReadBufferSize;

            while (_isRunning && !token.IsCancellationRequested)
            {
                byte[]? buffer = null;
                try
                {
                    if (!_port.IsOpen)
                    {
                        RecordDiagnosticEvent("reconnect", "read loop detected closed port");
                        if (!await TryReconnectAsync(token, "port closed").ConfigureAwait(false))
                        {
                            break;
                        }
                        continue;
                    }

                    buffer = ArrayPool<byte>.Shared.Rent(readSize);
                    int count = await _port.BaseStream.ReadAsync(buffer.AsMemory(0, readSize), token).ConfigureAwait(false);

                    if (count > 0)
                    {
                        // 步骤1：统计原始字节并按批次记录接收内容。
                        // 为什么：用于定位"是否收到字节但未解析成包"的问题。
                        // 风险点：高频十六进制日志会增加 IO 与 CPU 开销。
                        Interlocked.Add(ref _rawBytesSinceLastLog, count);
                        var seq = Interlocked.Increment(ref _rawReadChunkSeq);
                        var totalBytes = Interlocked.Add(ref _rawReadByteTotal, count);

                        if (RuntimeOptions.EnableRawReadChunkLog)
                        {
                            Logger.AddLog(
                                LogLevel.Information,
                                "[IO Read Chunk] Port={Port}, Seq={Seq}, Count={Count}, TotalBytes={TotalBytes}, Hex={Hex}",
                                args: new object[] { Name, seq, count, totalBytes, BitConverter.ToString(buffer, 0, count) });
                        }

                        var rented = new RentedBuffer(buffer, count);
                        buffer = null;

                        // 步骤2：写入背压通道，满则异步等待。
                        // 为什么：通道起到削峰填谷与流控作用。
                        // 风险点：通道关闭或取消时需归还已租借的数组。
                        if (!_rawInputChannel.Writer.TryWrite(rented))
                        {
                            try
                            {
                                await _rawInputChannel.Writer.WriteAsync(rented, token).ConfigureAwait(false);
                            }
                            catch (ChannelClosedException)
                            {
                                rented.Dispose();
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                rented.Dispose();
                                break;
                            }
                            catch
                            {
                                rented.Dispose();
                                throw;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning && !token.IsCancellationRequested)
                    {
                        Logger.AddLog(LogLevel.Error, $"[IO Error] {ex.Message}", exception: ex);
                        RecordDiagnosticError("io-read", ex.Message);
                        if (!await TryReconnectAsync(token, "read failed").ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    if (buffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        }

        /// <summary>
        /// Stage 2: 解析任务 (Consumer)
        /// 从通道消费原始字节块，驱动状态机解析出完整业务对象。
        /// </summary>
        private async Task ParseLoop(CancellationToken token)
        {
            try
            {
                var resultList = new List<T>();

                await foreach (var chunk in _rawInputChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        // 步骤1：批量喂给解析器，逐字节状态机在 Parser.Parse 内部实现。
                        // 为什么：复用 Span 切片避免额外内存分配。
                        // 风险点：解析器状态异常会导致后续帧错位。
                        try
                        {
                            Parser.Parse(chunk.Buffer.AsSpan(0, chunk.Length), resultList);
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            Logger.AddLog(LogLevel.Error, $"[Parse Error] Port={Name}, ChunkLength={chunk.Length}, {ex.Message}", exception: ex);
                            RecordDiagnosticError("parse", ex.Message);
                            resultList.Clear();
                            continue;
                        }

                        if (resultList.Count > 0)
                        {
                            foreach (var result in resultList)
                            {
                                try
                                {
                                    OnParsed(result);
                                }
                                catch (Exception ex) when (!token.IsCancellationRequested)
                                {
                                    Logger.AddLog(LogLevel.Error, $"[OnParsed Error] Port={Name}, {ex.Message}", exception: ex);
                                    RecordDiagnosticError("on-parsed", ex.Message);
                                }

                                var handler = OnHandleChanged;
                                if (handler != null)
                                {
                                    DispatchParsedEvent(handler, result, token);
                                }
                            }
                            resultList.Clear();
                        }
                    }
                    finally
                    {
                        // 步骤2：消费完毕归还数组，防止内存泄漏。
                        // 为什么：数组来自 ArrayPool，必须显式归还。
                        // 风险点：遗漏归还会导致池耗尽，退化为 GC 分配。
                        chunk.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Stage 3: 发送循环，消费发送队列并写入串口 BaseStream。
        /// </summary>
        private async Task SendLoop(CancellationToken token)
        {
            try
            {
                await foreach (var msg in _sendChannel.Reader.ReadAllAsync(token))
                {
                    if (!_port.IsOpen && !await TryReconnectAsync(token, "write path detected closed port").ConfigureAwait(false))
                    {
                        Logger.AddLog(LogLevel.Error, $"[IO Write] Send dropped due to reconnect failure. Port={Name}, Bytes={msg.Data.Length}");
                        RecordDiagnosticError("io-write", $"send dropped after reconnect failure, bytes={msg.Data.Length}");
                        continue;
                    }

                    try
                    {
                        await _port.BaseStream.WriteAsync(msg.Data.AsMemory(0, msg.Data.Length), token).ConfigureAwait(false);
                        Volatile.Write(ref _lastSent, msg.Data);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        Logger.AddLog(LogLevel.Error, $"[IO Write] {ex.Message}", exception: ex);
                        RecordDiagnosticError("io-write", ex.Message);
                        await TryReconnectAsync(token, "write failed").ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Logger.AddLog(LogLevel.Error, $"[IO Write] {ex.Message}", exception: ex);
                RecordDiagnosticError("io-write", ex.Message);
            }
        }

        /// <summary>
        /// 诊断任务：每分钟统计并输出原始字节接收量。
        /// </summary>
        private async Task RawBytesLoggerLoop(CancellationToken token)
        {
            var intervalSeconds = RuntimeOptions.RawBytesLogIntervalSeconds;
            if (intervalSeconds <= 0)
            {
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token).ConfigureAwait(false);

                    var bytesCount = Interlocked.Exchange(ref _rawBytesSinceLastLog, 0);
                    if (bytesCount > 0)
                    {
                        Logger.AddLog(LogLevel.Information, $"[{Name}] Raw bytes received in last {intervalSeconds}s: {bytesCount:N0} bytes");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.AddLog(LogLevel.Error, $"[{Name}] RawBytesLoggerLoop failed: {ex.Message}", exception: ex);
            }
        }

        private void DispatchParsedEvent(EventHandler<object> handler, T result, CancellationToken token)
        {
            var operateResult = new OperateResult<T>(result, true, "Success");
            if (!RuntimeOptions.DispatchParsedEventAsync || _parsedEventChannel == null)
            {
                InvokeParsedEventHandler(handler, operateResult, token);
                return;
            }

            if (!_parsedEventChannel.Writer.TryWrite((handler, (object)operateResult)))
            {
                var dropped = Interlocked.Increment(ref _parsedEventDropCount);
                if (dropped == 1 || dropped % Math.Max(1, RuntimeOptions.SampleLogInterval) == 0)
                {
                    Logger.AddLog(LogLevel.Warning, $"[OnHandleChanged Drop] Port={Name}, Dropped={dropped}");
                    RecordDiagnosticError("event-drop", $"parsed event dropped count={dropped}");
                }
            }
        }

        private async Task ParsedEventDispatchLoopAsync(CancellationToken token)
        {
            if (_parsedEventChannel == null)
            {
                return;
            }

            try
            {
                await foreach (var item in _parsedEventChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (item is ValueTuple<EventHandler<object>, object> payload)
                    {
                        InvokeParsedEventHandler(payload.Item1, payload.Item2, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Logger.AddLog(LogLevel.Error, $"[ParsedEventDispatch Error] Port={Name}, {ex.Message}", exception: ex);
                RecordDiagnosticError("event-dispatch", ex.Message);
            }
        }

        private void InvokeParsedEventHandler(EventHandler<object> handler, object operateResult, CancellationToken token)
        {
            try
            {
                handler(this, operateResult);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Logger.AddLog(LogLevel.Error, $"[OnHandleChanged Error] Port={Name}, {ex.Message}", exception: ex);
                RecordDiagnosticError("event-handler", ex.Message);
            }
        }
    }
}
