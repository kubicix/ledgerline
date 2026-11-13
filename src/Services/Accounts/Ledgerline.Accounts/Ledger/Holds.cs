using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Ledger;

public sealed class Holds(AccountsDbContext db, ILedger ledger)
{
    public async Task<PostingResult> ReserveAsync(Guid operationId, Guid paymentId, Guid accountId, Money money, CancellationToken ct)
    {
        await LockPaymentAsync(paymentId, ct);
        if (await db.Holds.AnyAsync(x => x.PaymentId == paymentId, ct)) return new PostingResult.Rejected("HoldNotActive");
        return await ledger.PostAsync(Entries.Hold(operationId, paymentId, accountId, money), ct);
    }

    public async Task<PostingResult> CaptureAsync(Guid operationId, Guid paymentId, Guid merchantId, long amount, CancellationToken ct)
    {
        var hold = await LockHoldAsync(paymentId, ct);
        if (hold is null) return new PostingResult.Rejected("HoldNotFound");
        if (hold.Status != "Active") return new PostingResult.Rejected("HoldNotActive");
        if (amount > hold.AmountMinor) return new PostingResult.Rejected("AmountExceedsHold");
        return await ledger.PostAsync(Entries.Capture(operationId, paymentId, hold.AccountId, merchantId, hold.AmountMinor, amount, hold.Currency), ct);
    }

    public async Task<PostingResult> ReleaseAsync(Guid operationId, Guid paymentId, CancellationToken ct)
    {
        var hold = await LockHoldAsync(paymentId, ct);
        if (hold is null) return new PostingResult.Rejected("HoldNotFound");
        if (hold.Status != "Active") return new PostingResult.Rejected("HoldNotActive");
        return await ledger.PostAsync(Entries.Release(operationId, paymentId, hold.AccountId, Money.Create(hold.AmountMinor, hold.Currency)), ct);
    }

    private async Task<Hold?> LockHoldAsync(Guid paymentId, CancellationToken ct)
    {
        await LockPaymentAsync(paymentId, ct);
        return await db.Holds.FromSqlInterpolated($"SELECT * FROM holds WHERE payment_id = {paymentId} FOR UPDATE").SingleOrDefaultAsync(ct);
    }

    private Task<int> LockPaymentAsync(Guid paymentId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({paymentId.ToString()}, 0))", ct);

    internal static async Task ApplyProjectionAsync(AccountsDbContext db, PostingRequest request, CancellationToken ct)
    {
        if (request.EntryType == EntryType.Hold)
        {
            var debit = request.Lines.Single(x => x.Direction == Direction.Debit);
            db.Holds.Add(new Hold { PaymentId = request.PaymentId, AccountId = debit.AccountId,
                AmountMinor = debit.AmountMinor, Currency = request.Currency, Status = "Active" });
        }
        else if (request.EntryType is EntryType.Capture or EntryType.Release)
        {
            var hold = await db.Holds.SingleAsync(x => x.PaymentId == request.PaymentId, ct);
            hold.Status = request.EntryType == EntryType.Capture ? "Captured" : "Released";
        }
    }
}
