using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.OpenTelemetry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Logger.Internals
{
    internal static class OtlpReplayQueueManager
    {
        private static readonly string QueueDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "otlp-replay");
        private static readonly object QueueLock = new();
        private static readonly Queue<string> RecentEnqueueIds = new();
        private static readonly HashSet<string> RecentEnqueueIdSet = new(StringComparer.OrdinalIgnoreCase);
        private static readonly MessageTemplateParser TemplateParser = new();
        private static readonly MessageTemplate ReplayTemplate = TemplateParser.Parse("[Replay] {Message}");
        private static readonly MessageTemplate ReplayWithExTemplate = TemplateParser.Parse("[Replay] {Message} | Exception: {ReplayException}");

        private static string? _currentQueueFile;
        private static long _replayFailureCount;
        private static long _successfulReplayCount;
        private static long _lastSuccessfulReplayUtcTicks;
        private static long _lastReplayAttemptUtcTicks;
        private static long _maxFileSizeBytes = 50L * 1024 * 1024;
        private static long _maxTotalSizeBytes = 500L * 1024 * 1024;
        private static int _maxAgeHours = 72;
        private static OverflowStrategy _overflowStrategy = OverflowStrategy.DropOldest;
        private const int RecentEnqueueIdLimit = 8192;
        private static readonly TimeSpan ReplayRetryCooldown = TimeSpan.FromSeconds(15);

        private static string? _otlpLogsEndpoint;
        private static string? _otlpHeaders;
        private static Dictionary<string, object> _otlpResourceAttributes = new();
        private static Func<int>? _getOtlpFailureVersion;

        public static void Configure(long maxFileSizeBytes, long maxTotalSizeBytes, int maxAgeHours, string? overflowStrategy = null)
        {
            lock (QueueLock)
            {
                _maxFileSizeBytes = Math.Max(1L * 1024 * 1024, maxFileSizeBytes);
                _maxTotalSizeBytes = Math.Max(_maxFileSizeBytes, maxTotalSizeBytes);
                _maxAgeHours = Math.Max(1, maxAgeHours);
                _overflowStrategy = Enum.TryParse(overflowStrategy, true, out OverflowStrategy parsed)
                    ? parsed
                    : OverflowStrategy.DropOldest;
            }
        }

        public static void ConfigureOtlpReplay(
            string otlpLogsEndpoint,
            string? otlpHeaders,
            IDictionary<string, object> resourceAttributes,
            Func<int> getOtlpFailureVersion)
        {
            lock (QueueLock)
            {
                _otlpLogsEndpoint = otlpLogsEndpoint;
                _otlpHeaders = otlpHeaders;
                _otlpResourceAttributes = new Dictionary<string, object>(resourceAttributes);
                _getOtlpFailureVersion = getOtlpFailureVersion;
            }
        }

        public static void Enqueue(string level, string message, string? exception)
        {
            Enqueue(DateTime.UtcNow, level, message, exception);
        }

        public static void Enqueue(DateTime timestampUtc, string level, string message, string? exception)
        {
            var entry = new ReplayLogEntry
            {
                TimestampUtc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime(),
                Level = level,
                Message = message,
                Exception = exception,
                ReplayId = ComputeEventId(timestampUtc, level, message, exception)
            };

            try
            {
                lock (QueueLock)
                {
                    if (!TryRememberRecentEnqueueId(entry.ReplayId!))
                    {
                        return;
                    }

                    Directory.CreateDirectory(QueueDir);
                    CleanupExpiredFiles();
                    if (!EnforceSizeLimit())
                    {
                        return;
                    }

                    var filePath = GetOrCreateCurrentFile();
                    File.AppendAllText(filePath, JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
                    RotateFileIfNeeded(filePath);
                }
            }
            catch (Exception ex)
            {
                LogFallbackNotice($"补传队列写入失败: {ex.Message}");
            }
        }

        public static (int replayed, int failed) ReplayQueued(int maxBatchSize, Action<string> onNotice)
        {
            lock (QueueLock)
            {
                if (!Directory.Exists(QueueDir))
                {
                    return (0, 0);
                }

                RecoverTempQueueFiles();
                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderBy(info => info.CreationTimeUtc)
                    .ToList();

                if (files.Count == 0)
                {
                    return (0, 0);
                }

                var totalReplayed = 0;
                var totalFailed = 0;

                foreach (var fileInfo in files)
                {
                    var remainingBudget = maxBatchSize - totalReplayed;
                    if (remainingBudget <= 0)
                    {
                        break;
                    }

                    var (replayed, failed, hasRemaining) = ReplaySingleFile(fileInfo.FullName, remainingBudget);
                    totalReplayed += replayed;
                    totalFailed += failed;

                    if (!hasRemaining)
                    {
                        TryDeleteFile(fileInfo.FullName);
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

        public static QueueMetrics GetMetrics()
        {
            lock (QueueLock)
            {
                if (!Directory.Exists(QueueDir))
                {
                    return new QueueMetrics
                    {
                        ReplayFailureCount = Interlocked.Read(ref _replayFailureCount),
                        SuccessfulReplayCount = Interlocked.Read(ref _successfulReplayCount),
                        LastSuccessfulReplayUtc = ReadUtcTicks(ref _lastSuccessfulReplayUtcTicks),
                        LastReplayAttemptUtc = ReadUtcTicks(ref _lastReplayAttemptUtcTicks)
                    };
                }

                RecoverTempQueueFiles();
                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly);
                var tempFiles = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl.*.tmp", SearchOption.TopDirectoryOnly);
                var totalBytes = files.Sum(path => new FileInfo(path).Length);
                long totalLines = 0;
                long pendingConfirmationEntries = 0;
                DateTime? latestQueuedAttemptUtc = null;

                foreach (var file in files)
                {
                    try
                    {
                        foreach (var line in File.ReadLines(file, Encoding.UTF8))
                        {
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                continue;
                            }

                            totalLines++;
                            try
                            {
                                var entry = JsonSerializer.Deserialize<ReplayLogEntry>(line);
                                if (entry?.AttemptCount > 0)
                                {
                                    pendingConfirmationEntries++;
                                }

                                if (entry?.LastAttemptUtc.HasValue == true)
                                {
                                    var attemptUtc = entry.LastAttemptUtc.Value.Kind == DateTimeKind.Utc
                                        ? entry.LastAttemptUtc.Value
                                        : entry.LastAttemptUtc.Value.ToUniversalTime();

                                    if (!latestQueuedAttemptUtc.HasValue || attemptUtc > latestQueuedAttemptUtc.Value)
                                    {
                                        latestQueuedAttemptUtc = attemptUtc;
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                return new QueueMetrics
                {
                    FileCount = files.Length,
                    TempFileCount = tempFiles.Length,
                    TotalBytes = totalBytes,
                    TotalEntries = totalLines,
                    PendingConfirmationEntries = pendingConfirmationEntries,
                    ReplayFailureCount = Interlocked.Read(ref _replayFailureCount),
                    SuccessfulReplayCount = Interlocked.Read(ref _successfulReplayCount),
                    LastSuccessfulReplayUtc = ReadUtcTicks(ref _lastSuccessfulReplayUtcTicks),
                    LastReplayAttemptUtc = MaxUtc(ReadUtcTicks(ref _lastReplayAttemptUtcTicks), latestQueuedAttemptUtc)
                };
            }
        }

        private static (int replayed, int failed, bool hasRemaining) ReplaySingleFile(string filePath, int maxBatch)
        {
            var replayed = 0;
            var failed = 0;
            var remainingLines = new List<string>();
            try
            {
                foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (replayed + failed >= maxBatch)
                    {
                        remainingLines.Add(line);
                        continue;
                    }

                    try
                    {
                        var entry = JsonSerializer.Deserialize<ReplayLogEntry>(line);
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Message))
                        {
                            continue;
                        }

                        if (CanUseVerifiedReplayTransport() && TryVerifyBatchVisible(new[] { entry }))
                        {
                            MarkReplaySuccess();
                            replayed++;
                            continue;
                        }

                        if (entry.LastAttemptUtc.HasValue
                            && DateTime.UtcNow - entry.LastAttemptUtc.Value.ToUniversalTime() < ReplayRetryCooldown)
                        {
                            remainingLines.Add(JsonSerializer.Serialize(entry));
                            continue;
                        }

                        var useVerifiedReplayTransport = CanUseVerifiedReplayTransport();
                        var replayedSuccessfully = useVerifiedReplayTransport
                            ? TryReplayBatch(new[] { entry })
                            : ReplayBatchToCurrentLogger(new[] { entry });

                        if (replayedSuccessfully)
                        {
                            if (useVerifiedReplayTransport)
                            {
                                WriteReplayEventsToLocalLog(new[] { entry });
                            }

                            MarkReplaySuccess();
                            replayed++;
                        }
                        else
                        {
                            failed++;
                            MarkBatchForRetry(new[] { entry });
                            remainingLines.Add(JsonSerializer.Serialize(entry));
                        }
                    }
                    catch
                    {
                        failed++;
                        remainingLines.Add(line);
                    }
                }
            }
            catch
            {
                failed++;
                return (0, failed, true);
            }

            if (remainingLines.Count > 0)
            {
                RewriteQueueFileWithRemaining(filePath, remainingLines);
                return (replayed, failed, true);
            }

            return (replayed, failed, false);
        }

        private static bool TryReplayBatch(IReadOnlyList<ReplayLogEntry> batchEntries)
        {
            if (batchEntries.Count == 0 || string.IsNullOrWhiteSpace(_otlpLogsEndpoint) || _getOtlpFailureVersion == null)
            {
                return false;
            }

            var pendingEntries = GetEntriesPendingReplay(batchEntries);
            if (pendingEntries.Count == 0)
            {
                return true;
            }

            var beforeFailureVersion = _getOtlpFailureVersion();
            try
            {
                foreach (var entry in pendingEntries)
                {
                    Log.Write(BuildReplayLogEvent(entry, localOnly: false));
                }

                Thread.Sleep(1500);

                if (_getOtlpFailureVersion() != beforeFailureVersion)
                {
                    return false;
                }

                return TryVerifyBatchVisible(batchEntries);
            }
            catch
            {
                return false;
            }
        }

        private static bool CanUseVerifiedReplayTransport()
        {
            return !string.IsNullOrWhiteSpace(_otlpLogsEndpoint) && _getOtlpFailureVersion != null;
        }

        private static bool ReplayBatchToCurrentLogger(IReadOnlyList<ReplayLogEntry> batchEntries)
        {
            try
            {
                foreach (var entry in batchEntries)
                {
                    Log.Write(BuildReplayLogEvent(entry, localOnly: false));
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteReplayEventsToLocalLog(IReadOnlyList<ReplayLogEntry> batchEntries)
        {
            foreach (var entry in batchEntries)
            {
                try
                {
                    Log.Write(BuildReplayLogEvent(entry, localOnly: true));
                }
                catch
                {
                }
            }
        }

        private static bool TryVerifyBatchVisible(IReadOnlyList<ReplayLogEntry> batchEntries)
        {
            if (batchEntries.Count == 0)
            {
                return true;
            }

            if (!TryBuildOpenObserveSearchUri(out var searchUri))
            {
                return true;
            }

            var replayIds = batchEntries
                .Select(GetReplayId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (replayIds.Length == 0)
            {
                return true;
            }

            var minTimestamp = batchEntries.Min(entry => entry.TimestampUtc.Kind == DateTimeKind.Utc
                ? entry.TimestampUtc
                : DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc));
            var startTime = new DateTimeOffset(minTimestamp.AddMinutes(-10)).ToUnixTimeMilliseconds() * 1000;
            var endTime = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() * 1000;
            var replayIdList = string.Join(", ", replayIds.Select(id => $"'{id}'"));
            var sql = $"select count(distinct eventid) as c from \"default\" where eventid in ({replayIdList})";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (TryBuildAuthorizationHeader(out var authorizationHeader))
            {
                client.DefaultRequestHeaders.Authorization = authorizationHeader;
            }

            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        query = new
                        {
                            sql,
                            start_time = startTime,
                            end_time = endTime,
                            from = 0,
                            size = 10
                        }
                    });

                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var response = client.PostAsync(searchUri, content).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                    {
                        using var stream = response.Content.ReadAsStream();
                        using var document = JsonDocument.Parse(stream);
                        if (document.RootElement.TryGetProperty("hits", out var hits)
                            && hits.ValueKind == JsonValueKind.Array
                            && hits.GetArrayLength() > 0)
                        {
                            var first = hits[0];
                            if (first.TryGetProperty("c", out var countElement) && countElement.GetInt32() >= replayIds.Length)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch
                {
                }

                Thread.Sleep(500);
            }

            return false;
        }

        private static List<ReplayLogEntry> GetEntriesPendingReplay(IReadOnlyList<ReplayLogEntry> batchEntries)
        {
            try
            {
                var visibleIds = TryGetVisibleReplayIds(batchEntries);
                if (visibleIds.Count == 0)
                {
                    return batchEntries.ToList();
                }

                return batchEntries
                    .Where(entry => !visibleIds.Contains(GetReplayId(entry)))
                    .ToList();
            }
            catch
            {
                return batchEntries.ToList();
            }
        }

        private static HashSet<string> TryGetVisibleReplayIds(IReadOnlyList<ReplayLogEntry> batchEntries)
        {
            var replayIds = batchEntries
                .Select(GetReplayId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (replayIds.Length == 0)
            {
                return result;
            }

            if (!TryBuildOpenObserveSearchUri(out var searchUri))
            {
                return result;
            }

            var minTimestamp = batchEntries.Min(entry => entry.TimestampUtc.Kind == DateTimeKind.Utc
                ? entry.TimestampUtc
                : DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc));
            var startTime = new DateTimeOffset(minTimestamp.AddMinutes(-10)).ToUnixTimeMilliseconds() * 1000;
            var endTime = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() * 1000;
            var replayIdList = string.Join(", ", replayIds.Select(id => $"'{id}'"));
            var sql = $"select eventid from \"default\" where eventid in ({replayIdList}) group by eventid limit {replayIds.Length}";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (TryBuildAuthorizationHeader(out var authorizationHeader))
            {
                client.DefaultRequestHeaders.Authorization = authorizationHeader;
            }

            var payload = JsonSerializer.Serialize(new
            {
                query = new
                {
                    sql,
                    start_time = startTime,
                    end_time = endTime,
                    from = 0,
                    size = replayIds.Length
                }
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = client.PostAsync(searchUri, content).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return result;
            }

            using var stream = response.Content.ReadAsStream();
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var hit in hits.EnumerateArray())
            {
                if (hit.TryGetProperty("eventid", out var replayIdElement))
                {
                    var replayId = replayIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(replayId))
                    {
                        result.Add(replayId);
                    }
                }
            }

            return result;
        }

        private static LogEvent BuildReplayLogEvent(ReplayLogEntry entry, bool localOnly)
        {
            var ts = entry.TimestampUtc.Kind == DateTimeKind.Utc
                ? entry.TimestampUtc
                : DateTime.SpecifyKind(entry.TimestampUtc, DateTimeKind.Utc);
            var timestamp = new DateTimeOffset(ts);
            var properties = new List<LogEventProperty>
            {
                new("ReplayQueue", new ScalarValue(true)),
                new("ReplayTimestampUtc", new ScalarValue(entry.TimestampUtc)),
                new("Message", new ScalarValue(entry.Message)),
                new("EventId", new ScalarValue(GetReplayId(entry))),
                new("ReplayId", new ScalarValue(GetReplayId(entry))),
            };

            if (localOnly)
            {
                properties.Add(new LogEventProperty("ReplayLocalOnly", new ScalarValue(true)));
            }

            MessageTemplate template;
            if (!string.IsNullOrWhiteSpace(entry.Exception))
            {
                properties.Add(new LogEventProperty("ReplayException", new ScalarValue(entry.Exception)));
                template = ReplayWithExTemplate;
            }
            else
            {
                template = ReplayTemplate;
            }

            return new LogEvent(timestamp, ParseSerilogLevel(entry.Level), null, template, properties);
        }

        private static Dictionary<string, string> ParseHeaders(string headers)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = segment.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex == segment.Length - 1)
                {
                    continue;
                }

                var key = segment[..separatorIndex].Trim();
                var value = segment[(separatorIndex + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = value;
                }
            }

            return result;
        }

        private static bool TryBuildAuthorizationHeader(out AuthenticationHeaderValue? headerValue)
        {
            headerValue = null;
            if (string.IsNullOrWhiteSpace(_otlpHeaders))
            {
                return false;
            }

            var headers = ParseHeaders(_otlpHeaders);
            if (!headers.TryGetValue("Authorization", out var rawAuthorization) || string.IsNullOrWhiteSpace(rawAuthorization))
            {
                return false;
            }

            var parts = rawAuthorization.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            headerValue = new AuthenticationHeaderValue(parts[0], parts[1]);
            return true;
        }

        private static bool TryBuildOpenObserveSearchUri(out Uri? searchUri)
        {
            searchUri = null;
            if (!Uri.TryCreate(_otlpLogsEndpoint, UriKind.Absolute, out var logsUri))
            {
                return false;
            }

            var segments = logsUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 4 || !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var orgId = segments[1];
            var builder = new UriBuilder(logsUri.Scheme, logsUri.Host, logsUri.Port, $"/api/{orgId}/_search")
            {
                Query = "is_ui_histogram=false&is_multi_stream_search=false&validate=false"
            };

            searchUri = builder.Uri;
            return true;
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
                RecoverTempQueueFiles();
                var cutoff = DateTime.UtcNow.AddHours(-_maxAgeHours);
                foreach (var file in Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    if (new FileInfo(file).CreationTimeUtc < cutoff)
                    {
                        TryDeleteFile(file);
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
                RecoverTempQueueFiles();
                var files = Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderBy(info => info.CreationTimeUtc)
                    .ToList();

                var totalSize = files.Sum(info => info.Length);
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
                    LogFallbackNotice("补传队列超过容量上限，继续保留全部队列文件");
                    return true;
                }

                var deleted = 0;
                foreach (var file in files)
                {
                    TryDeleteFile(file.FullName);
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

        private static void RewriteQueueFileWithRemaining(string filePath, List<string> remainingLines)
        {
            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllLines(tempPath, remainingLines, Encoding.UTF8);
            try
            {
                File.Copy(tempPath, filePath, true);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        private static void RecoverTempQueueFiles()
        {
            foreach (var tempPath in Directory.GetFiles(QueueDir, "otlp-replay.*.jsonl.*.tmp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var markerIndex = tempPath.IndexOf(".jsonl.", StringComparison.OrdinalIgnoreCase);
                    if (markerIndex < 0)
                    {
                        continue;
                    }

                    var queuePath = tempPath[..(markerIndex + ".jsonl".Length)];
                    File.Copy(tempPath, queuePath, true);
                    TryDeleteFile(tempPath);
                }
                catch
                {
                }
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }

        private static LogEventLevel ParseSerilogLevel(string? level)
        {
            return Enum.TryParse(level, true, out LogEventLevel parsed)
                ? parsed
                : LogEventLevel.Information;
        }

        private static bool TryRememberRecentEnqueueId(string replayId)
        {
            if (string.IsNullOrWhiteSpace(replayId))
            {
                return true;
            }

            if (!RecentEnqueueIdSet.Add(replayId))
            {
                return false;
            }

            RecentEnqueueIds.Enqueue(replayId);
            while (RecentEnqueueIds.Count > RecentEnqueueIdLimit)
            {
                var removed = RecentEnqueueIds.Dequeue();
                RecentEnqueueIdSet.Remove(removed);
            }

            return true;
        }

        private static void MarkBatchForRetry(IEnumerable<ReplayLogEntry> entries)
        {
            var attemptedAt = DateTime.UtcNow;
            WriteUtcTicks(ref _lastReplayAttemptUtcTicks, attemptedAt);
            foreach (var entry in entries)
            {
                entry.AttemptCount++;
                entry.LastAttemptUtc = attemptedAt;
            }
        }

        private static void MarkReplaySuccess()
        {
            Interlocked.Increment(ref _successfulReplayCount);
            WriteUtcTicks(ref _lastSuccessfulReplayUtcTicks, DateTime.UtcNow);
        }

        private static void WriteUtcTicks(ref long targetTicks, DateTime utcTime)
        {
            var normalized = utcTime.Kind == DateTimeKind.Utc ? utcTime : utcTime.ToUniversalTime();
            Interlocked.Exchange(ref targetTicks, normalized.Ticks);
        }

        private static DateTime? ReadUtcTicks(ref long sourceTicks)
        {
            var ticks = Interlocked.Read(ref sourceTicks);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }

        private static DateTime? MaxUtc(DateTime? left, DateTime? right)
        {
            if (!left.HasValue)
            {
                return right;
            }

            if (!right.HasValue)
            {
                return left;
            }

            return left.Value >= right.Value ? left : right;
        }

        private static string GetReplayId(ReplayLogEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.ReplayId))
            {
                return entry.ReplayId;
            }

            entry.ReplayId = ComputeEventId(
                entry.TimestampUtc,
                entry.Level ?? string.Empty,
                entry.Message ?? string.Empty,
                entry.Exception);
            return entry.ReplayId;
        }

        private static string ComputeEventId(DateTime timestampUtc, string level, string message, string? exception)
        {
            var normalizedTimestamp = timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime();
            var seed = string.Join("|",
                normalizedTimestamp.ToString("O"),
                level,
                message,
                exception ?? string.Empty);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void LogFallbackNotice(string message)
        {
            try
            {
                var fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "fallback.log");
                File.AppendAllText(
                    fallbackPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Logger] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        private enum OverflowStrategy
        {
            DropOldest,
            RejectNew,
            WarnOnly
        }

        internal sealed class ReplayLogEntry
        {
            public DateTime TimestampUtc { get; set; }
            public string? Level { get; set; }
            public string? Message { get; set; }
            public string? Exception { get; set; }
            public string? ReplayId { get; set; }
            public int AttemptCount { get; set; }
            public DateTime? LastAttemptUtc { get; set; }
        }

        public sealed class QueueMetrics
        {
            public int FileCount { get; set; }
            public int TempFileCount { get; set; }
            public long TotalBytes { get; set; }
            public long TotalEntries { get; set; }
            public long PendingConfirmationEntries { get; set; }
            public long ReplayFailureCount { get; set; }
            public long SuccessfulReplayCount { get; set; }
            public DateTime? LastSuccessfulReplayUtc { get; set; }
            public DateTime? LastReplayAttemptUtc { get; set; }
        }
    }
}
