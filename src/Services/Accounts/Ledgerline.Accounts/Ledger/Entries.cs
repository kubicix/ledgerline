using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;

namespace Ledgerline.Accounts.Ledger;

public static class Entries
{
    public static PostingRequest TopUp(Guid opId, Guid paymentId, Guid walletId, Money money) =>
        Create(opId, paymentId, EntryType.TopUp, money.Currency,
            [new(SystemAccounts.IdFor(SystemAccounts.BankClearing, money.Currency), Direction.Debit, money.AmountMinor),
             new(walletId, Direction.Credit, money.AmountMinor)]);

    public static PostingRequest Transfer(Guid opId, Guid paymentId, Guid fromId, Guid toId, Money money) =>
        Create(opId, paymentId, EntryType.Transfer, money.Currency,
            [new(fromId, Direction.Debit, money.AmountMinor), new(toId, Direction.Credit, money.AmountMinor)]);

    public static PostingRequest Hold(Guid opId, Guid paymentId, Guid walletId, Money money) =>
        Create(opId, paymentId, EntryType.Hold, money.Currency,
            [new(walletId, Direction.Debit, money.AmountMinor),
             new(SystemAccounts.IdFor(SystemAccounts.PaymentsPending, money.Currency), Direction.Credit, money.AmountMinor)]);

    public static PostingRequest Capture(Guid opId, Guid paymentId, Guid customerId, Guid merchantId,
        long holdMinor, long captureMinor, string currency)
    {
        var lines = new List<PostingLine>
        {
            new(SystemAccounts.IdFor(SystemAccounts.PaymentsPending, currency), Direction.Debit, holdMinor),
            new(merchantId, Direction.Credit, captureMinor)
        };
        var remainder = checked(holdMinor - captureMinor);
        if (remainder > 0) lines.Add(new(customerId, Direction.Credit, remainder));
        return Create(opId, paymentId, EntryType.Capture, currency, lines, new HashSet<Guid> { customerId });
    }

    public static PostingRequest Release(Guid opId, Guid paymentId, Guid customerId, Money money) =>
        Create(opId, paymentId, EntryType.Release, money.Currency,
            [new(SystemAccounts.IdFor(SystemAccounts.PaymentsPending, money.Currency), Direction.Debit, money.AmountMinor),
             new(customerId, Direction.Credit, money.AmountMinor)], new HashSet<Guid> { customerId });

    public static PostingRequest Refund(Guid opId, Guid paymentId, Guid merchantId, Guid customerId, Money money) =>
        Create(opId, paymentId, EntryType.Refund, money.Currency,
            [new(merchantId, Direction.Debit, money.AmountMinor), new(customerId, Direction.Credit, money.AmountMinor)]);

    private static PostingRequest Create(Guid opId, Guid paymentId, EntryType type, string currency,
        IReadOnlyList<PostingLine> lines, IReadOnlySet<Guid>? skipStatus = null) =>
        new(opId, paymentId, type, currency, lines, skipStatus ?? new HashSet<Guid>());
}
