using Logger.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortService.Services
{
    public abstract partial class PortContext<T>
    {
        /// <summary>
        /// 尝试重连串口，支持强制重开与退避重试。
        /// </summary>
        protected async Task<bool> TryReconnectAsync(CancellationToken token, string reason, bool forceReopen = false)
        {
            Volatile.Write(ref _lastReconnectReason, reason);
            Interlocked.Exchange(ref _lastReconnectUtcTicks, DateTime.UtcNow.Ticks);
            RecordDiagnosticEvent("reconnect", $"start: {reason}");

            var reconnectPolicy = ReconnectPolicyOptions.From(RuntimeOptions);
            var maxAttempts = reconnectPolicy.MaxReconnectAttempts;
            var intervalMs = reconnectPolicy.ReconnectIntervalMs;

            // 步骤1：按最大次数执行重连尝试。
            // 为什么：给短暂链路故障留恢复窗口。
            // 风险点：无限重试会造成线程长期占用。
            for (int attempt = 1; attempt <= maxAttempts && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    if (!_isRunning) return false;

                    if (_port.IsOpen && !forceReopen)
                    {
                        return true;
                    }

                    if (_port.IsOpen && forceReopen)
                    {
                        try { _port.Close(); } catch { }
                    }

                    // 步骤2：重连成功后立即记录结果并返回。
                    // 为什么：尽快恢复读写链路，减少业务中断。
                    // 风险点：成功后不及时返回会产生多余重连动作。
                    _port.Open();
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Reconnected successfully. Reason={reason}, Attempt={attempt}/{maxAttempts}");
                    RecordDiagnosticEvent("reconnect", $"success: {reason}, attempt={attempt}/{maxAttempts}");
                    ObserveReconnectOutcome(isExhausted: false);
                    return true;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Logger.AddLog(LogLevel.Warning, $"[{Name}] Reconnect failed. Reason={reason}, Attempt={attempt}/{maxAttempts}, Error={ex.Message}");
                    RecordDiagnosticError("reconnect", $"failed: {reason}, attempt={attempt}/{maxAttempts}, error={ex.Message}");
                }

                // 步骤3：失败后按间隔退避。
                // 为什么：避免连续重试造成设备或总线压力。
                // 风险点：无退避会触发重连风暴。
                if (attempt < maxAttempts)
                {
                    try
                    {
                        await Task.Delay(intervalMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }

            // 步骤4：重连耗尽后记录失败告警。
            // 为什么：给上层提供可观测故障信号。
            // 风险点：无耗尽告警会导致故障长期隐蔽。
            Logger.AddLog(LogLevel.Error, $"[{Name}] Reconnect exhausted. Reason={reason}, MaxAttempts={maxAttempts}");
            RecordDiagnosticError("reconnect", $"exhausted: {reason}, maxAttempts={maxAttempts}");
            ObserveReconnectOutcome(isExhausted: true);
            return false;
        }

        private void ObserveReconnectOutcome(bool isExhausted)
        {
            var total = Interlocked.Increment(ref _reconnectCycleCount);
            if (isExhausted)
            {
                Interlocked.Increment(ref _reconnectExhaustedCount);
            }

            var reconnectPolicy = ReconnectPolicyOptions.From(RuntimeOptions);
            var thresholdPercent = reconnectPolicy.FailureRateAlertThresholdPercent;
            var minSamples = reconnectPolicy.FailureRateAlertMinSamples;
            if (thresholdPercent <= 0 || minSamples <= 0)
            {
                return;
            }

            if (total < minSamples || total % minSamples != 0)
            {
                return;
            }

            var exhausted = Interlocked.Read(ref _reconnectExhaustedCount);
            var failureRatePercent = (double)exhausted * 100d / total;
            if (failureRatePercent >= thresholdPercent)
            {
                Logger.AddLog(LogLevel.Error, $"[{Name}] Reconnect failure-rate alert: {failureRatePercent:F2}% (exhausted={exhausted}, total={total}, threshold={thresholdPercent}%)");
                RecordDiagnosticError("reconnect", $"failure-rate alert: {failureRatePercent:F2}%");
            }
        }
    }
}
