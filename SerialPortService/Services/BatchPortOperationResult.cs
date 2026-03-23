namespace SerialPortService.Services
{
    public readonly record struct BatchPortOperationResult(
        int SuccessCount,
        int FailureCount,
        IReadOnlyList<Models.OperateResult> Results);
}

