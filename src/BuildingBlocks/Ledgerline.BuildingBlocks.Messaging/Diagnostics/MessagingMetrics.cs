using System.Diagnostics.Metrics;

namespace Ledgerline.BuildingBlocks.Messaging.Diagnostics;

/// <summary>
/// Outbox and inbox instruments of the ADR 0009 table, on the single meter <c>Ledgerline</c>.
/// Every measurement carries the <c>ledgerline.service</c> tag, so dashboards can split by service. The relay's lag and
/// pending measurements also carry <c>ledgerline.transport</c>, because each transport has its own relay loop and backlog.
/// The meter is static because the relay and consumer are also built by hand in tests, outside dependency injection.
/// </summary>
internal sealed class MessagingMetrics
{
    public const string MeterName = "Ledgerline";
    public const string ServiceTag = "ledgerline.service";
    public const string TransportTag = "ledgerline.transport";

    /// <summary>Second-scale boundaries; the SDK default boundaries are tuned for milliseconds. Declared first: static fields initialize in order.</summary>
    internal static readonly IReadOnlyList<double> SecondBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120];

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("ledgerline.outbox.lag", "s",
        "Time from outbox enqueue (in the committing transaction) to confirmed publish.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = SecondBuckets });
    private static readonly Gauge<long> Pending = Meter.CreateGauge<long>("ledgerline.outbox.pending", "{message}",
        "Outbox rows not yet confirmed by the broker.");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("ledgerline.inbox.duplicates", "{message}",
        "Deliveries absorbed by the inbox because the message was already handled.");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("ledgerline.consumer.retries", "{message}",
        "Deliveries sent to the retry queue after a handler failure.");
    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("ledgerline.consumer.dead_lettered", "{message}",
        "Deliveries sent to the dead queue.");

    private readonly KeyValuePair<string, object?> service;
    private readonly KeyValuePair<string, object?> transport;

    public MessagingMetrics(string service, string? transport = null)
    {
        this.service = new(ServiceTag, service);
        this.transport = new(TransportTag, transport);
    }

    public void RecordLag(TimeSpan lag) => Lag.Record(Math.Max(lag.TotalSeconds, 0), service, transport);
    public void RecordPending(long count) => Pending.Record(count, service, transport);
    public void Duplicate() => Duplicates.Add(1, service);
    public void Retried() => Retries.Add(1, service);
    public void DeadLettered() => DeadLetters.Add(1, service);
}
