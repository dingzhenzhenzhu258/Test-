using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Logger.Internals
{
    /// <summary>
    /// OTLP 补传队列管理器。
    /// 负责离线日志的分段存储、ACK 删除、大小上限控制与年龄清理。
    /// </summary>
    internal static class OtlpReplayQueueManager
    {
        private static readonly string QueueDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "otlp-replay");
        private static readonly object QueueLock = new();
        private static string? _currentQueueFile;
        private static long _replayFailureCount;
        private static OverflowStrategy _overflowStrategy = OverflowStrategy.DropOldest;

        // 步骤0：预编译补传消息模板，避免每条日志重复解析。
        // 为什么：MessageTemplateParser.Parse 有正则开销，缓存后复用可提升吞吐。
        // 风险点：模板占位符名称必须与 LogEventProperty 名称严格一致。
        private static readonly MessageTemplateParser _templateParser = new();
        private static readonly MessageTemplate _replayTemplate =
            _templateParser.Parse("[Replay] {Message}");
        private static readonly MessageTemplate _replayWithExTemplate =
            _templateParser.Parse("[Replay] {Message} | Exception: {ReplayException}");

        // 默认配置
        private static long _maxFileSizeBytes = 50L * 1024 * 1024; // 50MB
        private static long _maxTotalSizeBytes = 500L * 1024 * 1024; // 500MB
        private static int _maxAgeHours = 72; // 3 天

        /// <summary>
        /// 配置补传队列容量与保留策略。
        /// </summary>
        public static void Configure(long maxFileSizeBytes, long maxTotalSizeBytes, int maxAgeHours, string? overflowStrategy = null)
        {
            lock (QueueLock)
            {
                // 步骤1：对输入配置做最小边界保护。
                // 为什么：避免错误配置导致队列不可写或清理失效。
                // 风险点：配置过小会导致频繁轮转与删除，影响补传完整性。
                _maxFileSizeBytes = Math.Max(1L * 1024 * 1024, maxFileSizeBytes);
                _maxTotalSizeBytes = Math.Max(_maxFileSizeBytes, maxTotalSizeBytes);
                _maxAgeHours = Math.Max(1, maxAgeHours);

                // 步骤2：解析溢出策略。
                // 为什么：不同环境对“满队列时行为”诉求不同。
                // 风险点：策略配置错误会回退默认 DropOldest。
                _overflowStrategy = Enum.TryParse<OverflowStrategy>(overflowStrategy, true, out var parsed)
                    ? parsed
                    : OverflowStrategy.DropOldest;
            }
        }

        /// <summary>
        /// 入队一条日志到本地补传队列。
        /// </summary>
        public static void Enqueue(string level, string message, string? exception)
        {
            var entry = new ReplayLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level,
                Message = message,
                Exception = exception
            };

            try
            {
                lock (QueueLock)
                {
                    Directory.CreateDirectory(QueueDir);

                    // 步骤1：清理超龄文件。
                    // 为什么：防止过期日志无限堆积。
                    // 风险点：不清理会导致磁盘空间被历史队列占满。
                    CleanupExpiredFiles();

                    // 步骤2：执行总大小上限与溢出策略。
                    // 为什么：确保补传队列不会耗尽磁盘空间。
                    // 风险点：不限制会在长时间离线场景导致磁盘写满。
                    if (!EnforceSizeLimit())
                    {
                        return;
                    }

                    // 步骤3：获取或创建当前活跃队列文件。
                    // 为什么：单文件过大影响读写性能，分段提升效率。
                    // 风险点：不分段会导致单文件数百 MB 难以管理。
                    var filePath = GetOrCreateCurrentFile();
                    var line = JsonSerializer.Serialize(entry);
                    File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);

                    // 步骤4：检查当前文件大小并按需轮换。
                    // 为什么：控制单文件大小便于分批重放与删除。
                    // 风险点：不轮换会导致单文件持续增长。
                    RotateFileIfNeeded(filePath);
                }
            }
            catch (Exception ex)
            {
                // 补传队列写入失败不应影响主流程，静默记录。
                try
                {
                    var fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "fallback.log");
                    File.AppendAllText(fallbackPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Logger] 补传队列写入失败: {ex.Message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 重放补传队列中的离线日志。
        /// </summary>
        /// <param name="maxBatchSize">最大重放条数</param>
        /// <param name="onNotice">告警回调</param>
        /// <returns>(replayed, failed)</returns>
        public static (int replayed, int failed) ReplayQueued(int maxBatchSize, Action<string> onNotice)
        {
            lock (QueueLock)
            {
                if (!Directory.Exists(QueueDir))
                {
                    return (0, 0);
                }

                // 步骤1：按时间顺序重放所有队列文件。
                // 为什么：确保先入先出，保持日志时序。
                // 风险点：不排序会导致新日志先重放，旧日志后重放。
                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.CreationTimeUtc)
                    .ToList();

                if (files.Count == 0)
                {
                    return (0, 0);
                }

                var totalReplayed = 0;
                var totalFailed = 0;

                foreach (var fileInfo in files)
                {
                    // 步骤2：分批重放单个文件。
                    // 为什么：避免一次性加载超大文件到内存。
                    // 风险点：全量加载会在百万日志场景触发 OOM。
                    var (replayed, failed) = ReplaySingleFile(fileInfo.FullName, maxBatchSize - totalReplayed);
                    totalReplayed += replayed;
                    totalFailed += failed;

                    // 步骤3：重放成功则 ACK 删除该文件。
                    // 为什么：确保不丢失（先发送成功再删除）。
                    // 风险点：若先删除后发送，发送失败会导致永久丢失。
                    if (failed == 0)
                    {
                        try
                        {
                            File.Delete(fileInfo.FullName);
                        }
                        catch
                        {
                        }
                    }

                    if (totalReplayed >= maxBatchSize)
                    {
                        break;
                    }
                }

                if (totalReplayed > 0)
                {
                    onNotice($"已重放 {totalReplayed} 条离线日志，失败 {totalFailed} 条");
                }

                if (totalFailed > 0)
                {
                    Interlocked.Add(ref _replayFailureCount, totalFailed);
                    onNotice($"补传队列重放失败累计: {Interlocked.Read(ref _replayFailureCount)} 条");
                }

                return (totalReplayed, totalFailed);
            }
        }

        /// <summary>
        /// 获取队列统计指标。
        /// </summary>
        public static QueueMetrics GetMetrics()
        {
            lock (QueueLock)
            {
                if (!Directory.Exists(QueueDir))
                {
                    return new QueueMetrics();
                }

                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly);
                var totalBytes = files.Sum(f => new FileInfo(f).Length);
                var totalLines = 0L;

                foreach (var file in files)
                {
                    try
                    {
                        totalLines += File.ReadLines(file, Encoding.UTF8).Count();
                    }
                    catch
                    {
                    }
                }

                return new QueueMetrics
                {
                    FileCount = files.Length,
                    TotalBytes = totalBytes,
                    TotalEntries = totalLines,
                    ReplayFailureCount = Interlocked.Read(ref _replayFailureCount)
                };
            }
        }

        private static string GetOrCreateCurrentFile()
        {
            if (!string.IsNullOrWhiteSpace(_currentQueueFile) && File.Exists(_currentQueueFile))
            {
                return _currentQueueFile;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            _currentQueueFile = Path.Combine(QueueDir, $"otlp-replay.{timestamp}.jsonl");
            return _currentQueueFile;
        }

        private static void RotateFileIfNeeded(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists && fileInfo.Length >= _maxFileSizeBytes)
            {
                _currentQueueFile = null;
            }
        }

        private static void CleanupExpiredFiles()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-_maxAgeHours);
                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly);

                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTimeUtc < cutoff)
                    {
                        File.Delete(file);
                        LogFallbackNotice($"已删除超龄补传队列文件: {Path.GetFileName(file)}");
                    }
                }
            }
            catch
            {
            }
        }

        private static bool EnforceSizeLimit()
        {
            try
            {
                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.CreationTimeUtc)
                    .ToList();

                var totalSize = files.Sum(f => f.Length);
                if (totalSize <= _maxTotalSizeBytes)
                {
                    return true;
                }

                if (_overflowStrategy == OverflowStrategy.RejectNew)
                {
                    LogFallbackNotice("补传队列达到容量上限，已按 RejectNew 策略丢弃新日志入队请求");
                    return false;
                }

                if (_overflowStrategy == OverflowStrategy.WarnOnly)
                {
                    LogFallbackNotice("补传队列超过容量上限（WarnOnly），继续保留全部队列文件");
                    return true;
                }

                // 溢出策略：删除最旧文件直到总大小低于上限
                var deleted = 0;
                foreach (var file in files)
                {
                    File.Delete(file.FullName);
                    totalSize -= file.Length;
                    deleted++;
                    if (totalSize <= _maxTotalSizeBytes)
                    {
                        break;
                    }
                }

                if (deleted > 0)
                {
                    LogFallbackNotice($"补传队列超限，已删除最旧的 {deleted} 个文件");
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private enum OverflowStrategy
        {
            DropOldest,
            RejectNew,
            WarnOnly
        }

        private static (int replayed, int failed) ReplaySingleFile(string filePath, int maxBatch)
        {
            var replayed = 0;
            var failed = 0;

            try
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<ReplayLogEntry>(line);
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Message))
                        {
                            continue;
                        }

                        var level = ParseSerilogLevel(entry.Level);

                        // 步骤1：将 DateTime 转为 DateTimeOffset，确保 Kind=Utc。
                        // 为什么：JSON 反序列化后 Kind 可能为 Unspecified，直接构造 DateTimeOffset 会偏移到本地时区。
                        // 风险点：Enqueue 端已保存 DateTime.UtcNow，这里强制 Utc 是安全的。
                        var ts = entry.TimestampUtc.Kind == DateTimeKind.Utc
                            ? entry.TimestampUtc
                            : DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc);
                        var timestamp = new DateTimeOffset(ts);

                        var properties = new List<LogEventProperty>
                        {
                            new("ReplayQueue", new ScalarValue(true)),
                            new("ReplayTimestampUtc", new ScalarValue(entry.TimestampUtc)),
                            new("Message", new ScalarValue(entry.Message))
                        };

                        MessageTemplate template;
                        if (!string.IsNullOrWhiteSpace(entry.Exception))
                        {
                            properties.Add(new LogEventProperty("ReplayException", new ScalarValue(entry.Exception)));
                            template = _replayWithExTemplate;
                        }
                        else
                        {
                            template = _replayTemplate;
                        }

                        // 步骤2：用原始时间戳构造 LogEvent，OTLP Sink 按此时间导出。
                        // 为什么：Log.Write(level, template, args) 总是使用 DateTimeOffset.Now，补传日志会全部堆叠到当前时刻。
                        // 风险点：MessageTemplate 占位符必须与 properties 名称一一对应，否则渲染为空。
                        var logEvent = new LogEvent(timestamp, level, null, template, properties);
                        Log.Write(logEvent);

                        replayed++;
                        if (replayed >= maxBatch)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }
            catch
            {
                failed = 1;
            }

            return (replayed, failed);
        }

        private static LogEventLevel ParseSerilogLevel(string? level)
        {
            return Enum.TryParse<LogEventLevel>(level, true, out var parsed)
                ? parsed
                : LogEventLevel.Information;
        }

        private static void LogFallbackNotice(string message)
        {
            try
            {
                var fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "fallback.log");
                File.AppendAllText(fallbackPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Logger] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        internal sealed class ReplayLogEntry
        {
            public DateTime TimestampUtc { get; set; }
            public string? Level { get; set; }
            public string? Message { get; set; }
            public string? Exception { get; set; }
        }

        public sealed class QueueMetrics
        {
            public int FileCount { get; set; }
            public long TotalBytes { get; set; }
            public long TotalEntries { get; set; }
            public long ReplayFailureCount { get; set; }
        }
    }
}
