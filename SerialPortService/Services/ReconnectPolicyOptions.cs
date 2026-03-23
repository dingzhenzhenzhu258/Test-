namespace SerialPortService.Services
{
    public readonly record struct ReconnectPolicyOptions(
        int ReconnectIntervalMs,
        int MaxReconnectAttempts,
        int FailureRateAlertThresholdPercent,
        int FailureRateAlertMinSamples)
    {
        public static ReconnectPolicyOptions From(Handler.GenericHandlerOptions options)
            => new(
                options.ReconnectIntervalMs,
                options.MaxReconnectAttempts,
                options.ReconnectFailureRateAlertThresholdPercent,
                options.ReconnectFailureRateAlertMinSamples);
    }
}

