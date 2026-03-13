namespace SerialPortService.Services.Handler
{
    /// <summary>
    /// 通用处理器指标快照。
    /// 可用于周期上报、看板展示与告警阈值计算。
    /// </summary>
    public readonly record struct GenericHandlerMetrics(
        long IdleDropped,
        long OverflowDropped,
        long Unmatched,
        long RetryCount,
        long TimeoutCount,
        long MatchedCount,
        long TotalLatencyMs,
        int ActiveRequests,
        long WaitBacklog,
        long WaitBacklogHighWatermark)
    {
        public double AverageLatencyMs => MatchedCount == 0 ? 0 : (double)TotalLatencyMs / MatchedCount;
    }
}
