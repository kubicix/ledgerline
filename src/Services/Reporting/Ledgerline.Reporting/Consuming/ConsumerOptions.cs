using System.Globalization;

namespace Ledgerline.Reporting.Consuming;

/// <summary>Settings of the Kafka consumer loop and the dead-letter producer, from the <c>Reporting:Kafka</c> section.</summary>
public sealed class ConsumerOptions
{
    public const string BootstrapServersKey = "Reporting:Kafka:BootstrapServers";
    public const string GroupIdKey = "Reporting:Kafka:GroupId";
    public const string LedgerEntriesTopicKey = "Reporting:Kafka:LedgerEntriesTopic";
    public const string PaymentEventsTopicKey = "Reporting:Kafka:PaymentEventsTopic";
    public const string AccountEventsTopicKey = "Reporting:Kafka:AccountEventsTopic";
    public const string DeadLetterTopicKey = "Reporting:Kafka:DeadLetterTopic";
    public const string MaxBatchSizeKey = "Reporting:Kafka:MaxBatchSize";
    public const string RetryInitialDelayMsKey = "Reporting:Kafka:RetryInitialDelayMs";
    public const string RetryMaxDelayMsKey = "Reporting:Kafka:RetryMaxDelayMs";

    public required string BootstrapServers { get; init; }
    public string GroupId { get; init; } = "reporting";
    public string LedgerEntriesTopic { get; init; } = "ledgerline.ledger.entries.v1";
    public string PaymentEventsTopic { get; init; } = "ledgerline.payments.events.v1";
    public string AccountEventsTopic { get; init; } = "ledgerline.accounts.events.v1";
    public string DeadLetterTopic { get; init; } = "ledgerline.reporting.dead.v1";

    /// <summary>Most records applied before one offset commit.</summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>First wait after a failure; it doubles per consecutive failure up to <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The topics the loop subscribes to; the dead topic is only produced to.</summary>
    public IReadOnlyList<string> EventTopics => [LedgerEntriesTopic, PaymentEventsTopic, AccountEventsTopic];

    public TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(RetryMaxDelay.Ticks, RetryInitialDelay.Ticks * (1L << Math.Clamp(failures - 1, 0, 20))));

    public static ConsumerOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new ConsumerOptions { BootstrapServers = "" };
        return new ConsumerOptions
        {
            BootstrapServers = configuration[BootstrapServersKey] is { Length: > 0 } servers
                ? servers
                : throw new InvalidOperationException(BootstrapServersKey + " is required."),
            GroupId = configuration[GroupIdKey] ?? defaults.GroupId,
            LedgerEntriesTopic = configuration[LedgerEntriesTopicKey] ?? defaults.LedgerEntriesTopic,
            PaymentEventsTopic = configuration[PaymentEventsTopicKey] ?? defaults.PaymentEventsTopic,
            AccountEventsTopic = configuration[AccountEventsTopicKey] ?? defaults.AccountEventsTopic,
            DeadLetterTopic = configuration[DeadLetterTopicKey] ?? defaults.DeadLetterTopic,
            MaxBatchSize = configuration[MaxBatchSizeKey] is { } size ? int.Parse(size, CultureInfo.InvariantCulture) : defaults.MaxBatchSize,
            RetryInitialDelay = Milliseconds(configuration[RetryInitialDelayMsKey]) ?? defaults.RetryInitialDelay,
            RetryMaxDelay = Milliseconds(configuration[RetryMaxDelayMsKey]) ?? defaults.RetryMaxDelay
        };
    }

    private static TimeSpan? Milliseconds(string? value) =>
        value is null ? null : TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
}
