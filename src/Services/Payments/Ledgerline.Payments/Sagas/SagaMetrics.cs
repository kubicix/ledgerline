using System.Diagnostics.Metrics;

namespace Ledgerline.Payments.Sagas;

/// <summary>
/// Saga instruments of the ADR 0009 table, on the meter <c>Ledgerline</c>. The meter comes from the host's
/// <see cref="IMeterFactory"/>, so each host (and each test host) owns its instruments.
/// </summary>
public sealed class SagaMetrics
{
    private static readonly double[] SecondBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120];

    private readonly Histogram<double> duration;
    private readonly Counter<long> manualReviews;

    public SagaMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create("Ledgerline");
        duration = meter.CreateHistogram<double>("ledgerline.saga.duration", "s",
            "Time from payment creation to its final status, by payment kind and final status.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = SecondBuckets });
        manualReviews = meter.CreateCounter<long>("ledgerline.saga.manual_review", "{payment}",
            "Payments that ended in manual review. Should stay at zero.");
    }

    internal void Finished(PaymentKind kind, PaymentStatus outcome, TimeSpan elapsed) =>
        duration.Record(Math.Max(elapsed.TotalSeconds, 0), new("kind", kind.ToString()), new("outcome", outcome.ToString()));

    internal void ManualReview() => manualReviews.Add(1);
}
