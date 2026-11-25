using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Ledgerline.Testing;

/// <summary>
/// Collects measurements of selected instruments with a <see cref="MeterListener"/>.
/// Listeners are process-wide, so callers select instruments narrowly (by meter scope) or filter measurements by tag.
/// </summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly ConcurrentQueue<RecordedMeasurement> measurements = new();

    public MetricRecorder(Func<Instrument, bool> include)
    {
        ArgumentNullException.ThrowIfNull(include);
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (include(instrument)) meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        listener.Start();
    }

    /// <summary>Instruments of the meter named <paramref name="meterName"/>, from any meter scope.</summary>
    public static MetricRecorder ForMeter(string meterName) => new(instrument => instrument.Meter.Name == meterName);

    /// <summary>Instruments of the meter named <paramref name="meterName"/> created by one host's meter factory only.</summary>
    public static MetricRecorder ForMeter(string meterName, IMeterFactory scope) =>
        new(instrument => instrument.Meter.Name == meterName && ReferenceEquals(instrument.Meter.Scope, scope));

    public RecordedMeasurement[] Of(string instrument, Func<RecordedMeasurement, bool>? where = null) =>
        measurements.Where(m => m.Instrument == instrument && (where is null || where(m))).ToArray();

    /// <summary>Invokes the callbacks of enabled observable instruments, so their current values are recorded.</summary>
    public void RecordObservableInstruments() => listener.RecordObservableInstruments();

    public void Dispose() => listener.Dispose();

    private void Add(Instrument instrument, object value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags) copy[tag.Key] = tag.Value;
        measurements.Enqueue(new RecordedMeasurement(instrument.Name, instrument.Unit, value, copy));
    }
}

/// <summary>One measurement. <see cref="Value"/> keeps the instrument's own type (long or double).</summary>
public sealed record RecordedMeasurement(string Instrument, string? Unit, object Value, IReadOnlyDictionary<string, object?> Tags)
{
    public object? Tag(string key) => Tags.TryGetValue(key, out var value) ? value : null;
}
