namespace Ledgerline.Reconciliation.Infrastructure.Entities;

/// <summary>A row of <c>reconciliation_runs</c>: one run per business date.</summary>
public sealed class ReconciliationRun
{
    public Guid Id { get; set; }
    public DateOnly BusinessDate { get; set; }

    /// <summary>Running, Completed or Failed.</summary>
    public string Status { get; set; } = "";
    public int Matched { get; set; }
    public int Mismatched { get; set; }
    public bool InputsIncomplete { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
