using System.Diagnostics;

namespace Ledgerline.Testing;

public static class Eventually
{
    public static async Task ShouldAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var watch = Stopwatch.StartNew();
        do
        {
            if (await condition())
                return;
            var remaining = limit - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
        } while (watch.Elapsed < limit);
        throw new ShouldAssertException($"Condition did not become true within {limit}. {because}");
    }
}
