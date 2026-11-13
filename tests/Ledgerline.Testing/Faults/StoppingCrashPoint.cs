using Ledgerline.BuildingBlocks.Messaging;
using Microsoft.Extensions.Hosting;

namespace Ledgerline.Testing.Faults;

public sealed class StoppingCrashPoint(string name, IHostApplicationLifetime lifetime) : ICrashPoint
{
    private readonly string crashPointName = name;
    private readonly TaskCompletionSource crashed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int aborted;
    public Task Crashed => crashed.Task;

    public void Hit(string name)
    {
        if (Volatile.Read(ref aborted) == 0 && name != crashPointName) return;
        if (Interlocked.Exchange(ref aborted, 1) == 0)
        {
            lifetime.StopApplication();
            crashed.TrySetResult();
        }
        throw new HostAbortedException();
    }
}
