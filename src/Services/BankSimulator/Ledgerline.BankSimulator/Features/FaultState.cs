namespace Ledgerline.BankSimulator.Features;

public sealed record FaultDefinition(string Operation, string Mode, int Count, int? DelayMs = null);
public sealed record BankOperationMetadata(string Operation);

public sealed class FaultState
{
    private readonly object gate = new();
    private FaultDefinition? current;

    public void Set(FaultDefinition fault) { lock (gate) current = fault; }
    public void Clear() { lock (gate) current = null; }

    public FaultDefinition? TryTake(string operation)
    {
        lock (gate)
        {
            if (current is null || current.Operation != "Any" && current.Operation != operation) return null;
            var taken = current;
            current = taken.Count > 1 ? taken with { Count = taken.Count - 1 } : null;
            return taken;
        }
    }
}
