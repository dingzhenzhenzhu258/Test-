using Microsoft.Extensions.Logging;
using SerialPortService.Models;
using System.IO;
using System.Text;

namespace Test_High_speed_acquisition.Services
{
    /// <summary>
    /// 本地 Modbus 批量持久化服务。
    /// 提供文件滚动、失败重试与停止前刷盘保障。
    /// </summary>
    public sealed class ModbusPersistenceService
    {
        private const int MaxFileSizeBytes = 20 * 1024 * 1024;
        private const int MaxRetryCount = 3;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly string _baseDirectory;
        private readonly ILogger<ModbusPersistenceService> _logger;
        private long _persistedBatchCount;
        private long _persistedPacketCount;
        private long _persistFailureCount;
        private int _currentFileIndex;

        public ModbusPersistenceService(ILogger<ModbusPersistenceService> logger)
        {
            _logger = logger;
            _baseDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(_baseDirectory);
        }

        public long PersistedBatchCount => Interlocked.Read(ref _persistedBatchCount);
        public long PersistedPacketCount => Interlocked.Read(ref _persistedPacketCount);
        public long PersistFailureCount => Interlocked.Read(ref _persistFailureCount);

        public async Task PersistBatchAsync(IReadOnlyList<ModbusPacket> batch, CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
            {
                return;
            }

            var payload = BuildPayload(batch);
            Exception? lastException = null;

            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var filePath = await ResolveWritableFileAsync(payload.Length, cancellationToken).ConfigureAwait(false);
                        await File.AppendAllTextAsync(filePath, payload, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref _persistedBatchCount);
                        Interlocked.Add(ref _persistedPacketCount, batch.Count);
                        return;
                    }
                    catch (Exception ex) when (attempt < MaxRetryCount && !cancellationToken.IsCancellationRequested)
                    {
                        lastException = ex;
                        Interlocked.Increment(ref _persistFailureCount);
                        _logger.LogWarning(ex, "PersistBatchAsync retry {Attempt}/{MaxRetryCount} failed", attempt, MaxRetryCount);
                        await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        lastException = ex;
                        Interlocked.Increment(ref _persistFailureCount);
                        break;
                    }
                }
            }
            finally
            {
                _fileLock.Release();
            }

            throw new IOException("PersistBatchAsync failed after retries.", lastException);
        }

        private string BuildPayload(IReadOnlyList<ModbusPacket> batch)
        {
            var sb = new StringBuilder(batch.Count * 64);
            var timestamp = DateTime.Now;
            foreach (var packet in batch)
            {
                sb.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(',');
                sb.Append(packet.SlaveId);
                sb.Append(',');
                sb.Append(packet.FunctionCode);
                sb.Append(',');
                sb.AppendLine(BitConverter.ToString(packet.RawFrame));
            }

            return sb.ToString();
        }

        private async Task<string> ResolveWritableFileAsync(int incomingPayloadLength, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Path.Combine(_baseDirectory, $"modbus-{DateTime.Now:yyyyMMdd}-{_currentFileIndex:D3}.csv");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }

                var length = new FileInfo(candidate).Length;
                if (length + incomingPayloadLength < MaxFileSizeBytes)
                {
                    return candidate;
                }

                _currentFileIndex++;
                await Task.Yield();
            }
        }
    }
}
