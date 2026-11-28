namespace Ledgerline.Reconciliation.Runs;

/// <summary>The <c>Reconciliation:Run</c> section. Defaults per reconciliation.md section 3; tests may shorten them (handoff C3).</summary>
public sealed class RunOptions
{
    /// <summary>Step 3 gives up waiting for the Kafka consumers after this long and flags the run <c>inputs_incomplete</c>.</summary>
    public TimeSpan InputWaitTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan InputPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public static RunOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new RunOptions();
        return new RunOptions
        {
            InputWaitTimeout = configuration.GetValue<TimeSpan?>("Reconciliation:Run:InputWaitTimeout") ?? defaults.InputWaitTimeout,
            InputPollInterval = configuration.GetValue<TimeSpan?>("Reconciliation:Run:InputPollInterval") ?? defaults.InputPollInterval
        };
    }
}
