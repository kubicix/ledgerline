namespace Ledgerline.Payments.Sagas;

public sealed class TimeoutOptions
{
    public int StepTimeoutMs { get; init; } = 30_000;
    public int PollIntervalMs { get; init; } = 1_000;
    public int? CompensationTimeoutMs { get; init; }
}
