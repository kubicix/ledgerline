using System.Globalization;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>Settings of the staging consumer loop, from the <c>Reconciliation:Kafka</c> section.</summary>
public sealed class StagingOptions
{
    public const string BootstrapServersKey = "Reconciliation:Kafka:BootstrapServers";
    public const string GroupIdKey = "Reconciliation:Kafka:GroupId";
    public const string PaymentEventsTopicKey = "Reconciliation:Kafka:PaymentEventsTopic";
    public const string LedgerEntriesTopicKey = "Reconciliation:Kafka:LedgerEntriesTopic";
    public const string MaxBatchSizeKey = "Reconciliation:Kafka:MaxBatchSize";
    public const string RetryInitialDelayMsKey = "Reconciliation:Kafka:RetryInitialDelayMs";
    public const string RetryMaxDelayMsKey = "Reconciliation:Kafka:RetryMaxDelayMs";

    public required string BootstrapServers { get; init; }
    public string GroupId { get; init; } = "reconciliation";
    public string PaymentEventsTopic { get; init; } = "ledgerline.payments.events.v1";
    public string LedgerEntriesTopic { get; init; } = "ledgerline.ledger.entries.v1";

    /// <summary>Most records applied before one offset commit.</summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>First wait after a failure; it doubles per consecutive failure up to <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The topics the loop subscribes to.</summary>
    public IReadOnlyList<string> Topics => [PaymentEventsTopic, LedgerEntriesTopic];

    public TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(RetryMaxDelay.Ticks, RetryInitialDelay.Ticks * (1L << Math.Clamp(failures - 1, 0, 20))));

    public static StagingOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new StagingOptions { BootstrapServers = "" };
        return new StagingOptions
        {
            BootstrapServers = configuration[BootstrapServersKey] is { Length: > 0 } servers
                ? servers
                : throw new InvalidOperationException(BootstrapServersKey + " is required."),
            GroupId = configuration[GroupIdKey] ?? defaults.GroupId,
            PaymentEventsTopic = configuration[PaymentEventsTopicKey] ?? defaults.PaymentEventsTopic,
            LedgerEntriesTopic = configuration[LedgerEntriesTopicKey] ?? defaults.LedgerEntriesTopic,
            MaxBatchSize = configuration[MaxBatchSizeKey] is { } size ? int.Parse(size, CultureInfo.InvariantCulture) : defaults.MaxBatchSize,
            RetryInitialDelay = Milliseconds(configuration[RetryInitialDelayMsKey]) ?? defaults.RetryInitialDelay,
            RetryMaxDelay = Milliseconds(configuration[RetryMaxDelayMsKey]) ?? defaults.RetryMaxDelay
        };
    }

    private static TimeSpan? Milliseconds(string? value) =>
        value is null ? null : TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
}
