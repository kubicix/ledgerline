using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Reconciliation;
using Ledgerline.Reconciliation.Infrastructure;
using Ledgerline.Reconciliation.Infrastructure.Entities;
using Ledgerline.Reconciliation.Matching;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Reconciliation.Runs;

/// <summary>How one call of <see cref="ReconciliationRunner.RunAsync"/> ended.</summary>
public enum RunOutcome
{
    /// <summary>This call completed the run and enqueued its events.</summary>
    Completed,

    /// <summary>The date already has a Completed run with complete inputs; nothing was done.</summary>
    AlreadyCompleted,

    /// <summary>Another holder has the date's lock; nothing was done.</summary>
    LockNotAcquired,

    /// <summary>Another runner claimed the run meanwhile; this call wrote and published nothing.</summary>
    Superseded,

    /// <summary>The settlement file could not be fetched or parsed; the run is Failed with the error.</summary>
    Failed,
}

/// <summary>
/// One end-of-day run of a business date (reconciliation.md section 3), one method per step. At most one Completed run
/// per date (C1) is the database's guarantee, not the lock's: the run row is claimed with an insert on the unique date or
/// an optimistic update keyed by the claimant's <c>started_at</c>, and only the runner whose claim still stands may set the
/// run Completed or Failed. Items, the Completed row and every event are written in one transaction (outbox), so a
/// Completed run always has its events and a run that rolled back has none.
/// </summary>
public sealed partial class ReconciliationRunner(
    ReconciliationDbContext db, IOutbox outbox, IRunLock runLock, KafkaInputWait inputs, BankSettlementClient bank, IClock clock,
    ILogger<ReconciliationRunner> logger)
{
    public const int PaymentIdsPerEvent = 500;
    private const int MaxClaimAttempts = 3;
    private const string Running = "Running";
    private const string Completed = "Completed";
    private static readonly Guid ItemNamespace = Guid.Parse("3b9d1f0e-6a52-4c1e-8f7d-2c4a9e5b7d13");

    public async Task<RunOutcome> RunAsync(DateOnly businessDate, CancellationToken ct)
    {
        var held = await runLock.TryAcquireAsync(businessDate, ct);
        if (held is null)
        {
            LockHeld(logger, businessDate);
            return RunOutcome.LockNotAcquired;
        }
        await using (held)
        {
            var (claim, exit) = await ClaimAsync(businessDate, ct);
            return claim is null ? exit : await ReconcileAsync(claim, ct);
        }
    }

    /// <summary>Steps 3 to 7 for a run this runner has claimed.</summary>
    private async Task<RunOutcome> ReconcileAsync(Claim claim, CancellationToken ct)
    {
        var complete = await inputs.WaitAsync(claim.BusinessDate, ct);
        var settlement = await FetchSettlementAsync(claim.BusinessDate, ct);
        if (settlement.Error is { } error) return await FailAsync(claim, error, ct);
        var result = Matcher.Match(settlement.Records, await LoadPaymentsAsync(ct), await LoadLedgerAsync(claim.End, ct), claim.BusinessDate);
        return await CompleteAsync(claim, result, inputsIncomplete: !complete, ct);
    }

    /// <summary>
    /// Step 2. A new date is claimed by the insert. A Completed date with complete inputs exits: the day is done. A
    /// Running (crashed or concurrent) or Failed run, or a Completed one flagged <c>inputs_incomplete</c> (section 8:
    /// "the run completes and can be re-run"), is taken over by <see cref="ReclaimAsync"/>; losing that race to another
    /// claimant means deciding again. An incomplete Completed run is re-run only when this runner found it so on its
    /// first decision: after a lost race, the Completed run it then finds is another runner's fresh completion (or a
    /// re-run claimed and completed meanwhile), and re-running it at once would publish a second event set.
    /// </summary>
    private async Task<(Claim? Claim, RunOutcome Exit)> ClaimAsync(DateOnly businessDate, CancellationToken ct)
    {
        var firstDecision = true;
        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            if (await InsertRunAsync(businessDate, ct) is { } inserted) return (inserted, default);
            var existing = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.BusinessDate == businessDate, ct);
            if (existing is null) continue;
            if (existing.Status == Completed && (!existing.InputsIncomplete || !firstDecision)) return (null, RunOutcome.AlreadyCompleted);
            firstDecision = false;
            if (await ReclaimAsync(existing, ct) is { } reclaimed) return (reclaimed, default);
        }
        ClaimLost(logger, businessDate);
        return (null, RunOutcome.Superseded);
    }

    /// <summary>The insert of step 2; null on the unique violation of the business date.</summary>
    private async Task<Claim?> InsertRunAsync(DateOnly businessDate, CancellationToken ct)
    {
        var id = Ids.New();
        var startedAt = await db.Database.SqlQuery<DateTimeOffset>($"""
            INSERT INTO reconciliation_runs (id, business_date, status, started_at) VALUES ({id}, {businessDate}, 'Running', {clock.UtcNow})
            ON CONFLICT (business_date) DO NOTHING
            RETURNING started_at AS "Value"
            """).ToListAsync(ct);
        return startedAt.Count == 1 ? new Claim(id, businessDate, startedAt[0]) : null;
    }

    /// <summary>
    /// Takes over a Running, Failed or incomplete Completed run, the run id staying the same: an optimistic update on the observed
    /// <c>started_at</c> and status, which it moves strictly forward so no two claimants ever hold the same token, and the
    /// delete of the run's items, in one transaction. Null when the run changed since it was read: the status matters as
    /// well as the token, because completing a run keeps its <c>started_at</c>, so an observed Running run that was
    /// completed with incomplete inputs meanwhile must not be taken over.
    /// </summary>
    private async Task<Claim?> ReclaimAsync(ReconciliationRun observed, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var startedAt = await db.Database.SqlQuery<DateTimeOffset>($"""
            UPDATE reconciliation_runs
            SET status = 'Running', started_at = GREATEST({clock.UtcNow}, started_at + interval '1 microsecond'), error = NULL,
                inputs_incomplete = false, matched = 0, mismatched = 0, completed_at = NULL
            WHERE id = {observed.Id} AND status = {observed.Status} AND (status <> 'Completed' OR inputs_incomplete)
                AND started_at = {observed.StartedAt}
            RETURNING started_at AS "Value"
            """).ToListAsync(ct);
        if (startedAt.Count == 0) return null;
        await db.Database.ExecuteSqlAsync($"DELETE FROM reconciliation_items WHERE run_id = {observed.Id}", ct);
        await transaction.CommitAsync(ct);
        Reclaimed(logger, observed.BusinessDate, observed.Status);
        return new Claim(observed.Id, observed.BusinessDate, startedAt[0]);
    }

    /// <summary>Step 4: the bank's file, parsed. A fetch or parse failure is returned as the run's error text.</summary>
    private async Task<(IReadOnlyList<BankRecord> Records, string? Error)> FetchSettlementAsync(DateOnly businessDate, CancellationToken ct)
    {
        try
        {
            return (SettlementFileParser.Parse(await bank.GetSettlementAsync(businessDate, ct)), null);
        }
        catch (SettlementUnavailableException error)
        {
            return ([], error.Message);
        }
        catch (SettlementFormatException error)
        {
            return ([], error.Message);
        }
    }

    /// <summary>Every staged payment: the matcher filters by window, and an unknown bank reference needs all known ones.</summary>
    private Task<List<StagedPayment>> LoadPaymentsAsync(CancellationToken ct) =>
        db.StagedPayments.AsNoTracking()
            .Select(p => new StagedPayment(p.PaymentId, p.Kind, p.Status, p.AmountMinor, p.Currency, p.BankCaptureId, p.OccurredAt))
            .ToListAsync(ct);

    /// <summary>Every staged line posted before the end of the business date: the trial balance sums all of them.</summary>
    private Task<List<StagedLedgerLine>> LoadLedgerAsync(DateTimeOffset end, CancellationToken ct) =>
        db.StagedLedgerLines.AsNoTracking().Where(l => l.PostedAt < end)
            .Select(l => new StagedLedgerLine(l.JournalEntryId, l.LineNo, l.AccountId, l.IsBankClearing, l.EntryType, l.Direction,
                l.AmountMinor, l.Currency, l.PaymentId, l.PostedAt))
            .ToListAsync(ct);

    /// <summary>Sets the run Failed with the error, if this runner's claim still stands.</summary>
    private async Task<RunOutcome> FailAsync(Claim claim, string error, CancellationToken ct)
    {
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE reconciliation_runs SET status = 'Failed', error = {error}
            WHERE id = {claim.RunId} AND status = 'Running' AND started_at = {claim.StartedAt}
            """, ct);
        if (updated == 0) return Superseded(claim);
        RunFailed(logger, claim.BusinessDate, error);
        return RunOutcome.Failed;
    }

    /// <summary>
    /// Step 7, one transaction: the guarded update sets the run Completed only while this runner's claim stands (and
    /// locks the row until commit), then the items replace any left over and the events are enqueued. Everything
    /// commits together or not at all.
    /// </summary>
    private async Task<RunOutcome> CompleteAsync(Claim claim, ReconciliationResult result, bool inputsIncomplete, CancellationToken ct)
    {
        var completedAt = clock.UtcNow;
        completedAt = new DateTimeOffset(completedAt.UtcTicks / 10 * 10, TimeSpan.Zero);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE reconciliation_runs
            SET status = 'Completed', matched = {result.MatchedPaymentIds.Count}, mismatched = {result.Mismatches.Count},
                inputs_incomplete = {inputsIncomplete}, error = NULL, completed_at = {completedAt}
            WHERE id = {claim.RunId} AND status = 'Running' AND started_at = {claim.StartedAt}
            """, ct);
        if (updated == 0) return Superseded(claim);
        await db.Database.ExecuteSqlAsync($"DELETE FROM reconciliation_items WHERE run_id = {claim.RunId}", ct);
        db.Items.AddRange(result.Mismatches.Select((mismatch, index) => Item(claim.RunId, mismatch, index)));
        Enqueue(claim, result, inputsIncomplete, completedAt);
        // Not cancellable: a commit the server applies while the client reports cancellation would send a Completed run
        // to recovery, which for incomplete inputs re-runs it and publishes a second event set.
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        RunCompleted(logger, claim.BusinessDate, result.MatchedPaymentIds.Count, result.Mismatches.Count, inputsIncomplete);
        return RunOutcome.Completed;
    }

    /// <summary>One completed event, the matched ids in sorted batches of 500, one event per item; correlation = run id.</summary>
    private void Enqueue(Claim claim, ReconciliationResult result, bool inputsIncomplete, DateTimeOffset completedAt)
    {
        outbox.Enqueue(new ReconciliationCompleted(claim.RunId, claim.BusinessDate, result.MatchedPaymentIds.Count, result.Mismatches.Count,
            inputsIncomplete, completedAt), claim.RunId);
        foreach (var batch in result.MatchedPaymentIds.Order().Chunk(PaymentIdsPerEvent))
            outbox.Enqueue(new PaymentsReconciled(claim.RunId, claim.BusinessDate, batch), claim.RunId);
        foreach (var mismatch in result.Mismatches)
            outbox.Enqueue(new ReconciliationMismatchDetected(claim.RunId, claim.BusinessDate, mismatch.Code.ToString(), mismatch.Reference,
                mismatch.BankAmountMinor, mismatch.LedgerAmountMinor, mismatch.Currency), claim.RunId);
    }

    private RunOutcome Superseded(Claim claim)
    {
        RunSuperseded(logger, claim.BusinessDate);
        return RunOutcome.Superseded;
    }

    /// <summary>
    /// An item row. Its id is a UUIDv5 of the run id, the item's position in the matcher's sorted output, its code and
    /// reference: a re-run of the same inputs writes the very same rows (C2).
    /// </summary>
    private static ReconciliationItem Item(Guid runId, Mismatch mismatch, int index) => new()
    {
        Id = DeterministicId($"{runId}/{index}/{mismatch.Code}/{mismatch.Reference}"),
        RunId = runId,
        Code = mismatch.Code.ToString(),
        Reference = mismatch.Reference,
        BankAmountMinor = mismatch.BankAmountMinor,
        LedgerAmountMinor = mismatch.LedgerAmountMinor,
        Currency = mismatch.Currency
    };

    [SuppressMessage("Security", "CA5350", Justification = "UUIDv5 specifies SHA-1 for deterministic identifiers, not security.")]
    private static Guid DeterministicId(string name)
    {
        var hash = SHA1.HashData([.. ItemNamespace.ToByteArray(bigEndian: true), .. Encoding.UTF8.GetBytes(name)]);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <param name="StartedAt">The claim token: the <c>started_at</c> this runner wrote, as PostgreSQL stored it.</param>
    private sealed record Claim(Guid RunId, DateOnly BusinessDate, DateTimeOffset StartedAt)
    {
        /// <summary>The end of the business date: the next UTC midnight.</summary>
        public DateTimeOffset End => new(BusinessDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [LoggerMessage(1, LogLevel.Information, "The run of {BusinessDate} is locked by another holder; nothing to do")]
    private static partial void LockHeld(ILogger logger, DateOnly businessDate);
    [LoggerMessage(2, LogLevel.Information, "The {Status} run of {BusinessDate} was taken over for a re-run")]
    private static partial void Reclaimed(ILogger logger, DateOnly businessDate, string status);
    [LoggerMessage(3, LogLevel.Warning, "The run of {BusinessDate} could not be claimed; other runners kept taking it")]
    private static partial void ClaimLost(ILogger logger, DateOnly businessDate);
    [LoggerMessage(4, LogLevel.Information, "The run of {BusinessDate} was taken over by another runner; this one stops without writing")]
    private static partial void RunSuperseded(ILogger logger, DateOnly businessDate);
    [LoggerMessage(5, LogLevel.Warning, "The run of {BusinessDate} failed: {Error}")]
    private static partial void RunFailed(ILogger logger, DateOnly businessDate, string error);
    [LoggerMessage(6, LogLevel.Information, "The run of {BusinessDate} completed: {Matched} matched, {Mismatched} mismatched, inputs incomplete {InputsIncomplete}")]
    private static partial void RunCompleted(ILogger logger, DateOnly businessDate, int matched, int mismatched, bool inputsIncomplete);
}
