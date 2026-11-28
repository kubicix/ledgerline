namespace Ledgerline.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Wakes the relay after a commit that added outbox rows. Every waiter subscribes and has its own single permit, so
/// one <see cref="Notify"/> reaches every relay loop, and a notify that arrives while a loop is busy is kept until it waits.
/// </summary>
public sealed class OutboxSignal : IDisposable
{
    private readonly Lock gate = new();
    private Subscription[] subscriptions = [];

    public void Notify()
    {
        foreach (var subscription in Volatile.Read(ref subscriptions)) Release(subscription.Permit);
    }

    /// <summary>Registers one more waiter. Subscribe before the first scan, so no notify after that scan is missed.</summary>
    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);
        lock (gate) subscriptions = [.. subscriptions, subscription];
        return subscription;
    }

    public void Dispose()
    {
        foreach (var subscription in Volatile.Read(ref subscriptions)) subscription.Dispose();
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (gate) subscriptions = subscriptions.Where(x => x != subscription).ToArray();
    }

    private static void Release(SemaphoreSlim permit)
    {
        // A held permit already means a pending wake-up; only a concurrent release can still find it full below.
        if (permit.CurrentCount > 0) return;
        try { permit.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly OutboxSignal owner;

        internal Subscription(OutboxSignal owner) => this.owner = owner;

        internal SemaphoreSlim Permit { get; } = new(0, 1);

        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Permit.WaitAsync(timeout, ct);

        public void Dispose()
        {
            owner.Unsubscribe(this);
            Permit.Dispose();
        }
    }
}
