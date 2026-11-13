namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

public sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Notify()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => signal.WaitAsync(timeout, ct);
    public void Dispose() => signal.Dispose();
}
