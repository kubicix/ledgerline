using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using PostingLedger = Ledgerline.Accounts.Ledger.Ledger;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class LedgerPostingTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task TopUp_credits_wallet_and_debits_bank_clearing()
    {
        var (cs, wallet, _) = await SetupAsync();
        (await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), wallet, Money.Create(1000, "TRY")))).ShouldBeOfType<PostingResult.Posted>();
        await using var db = OpenDb(cs);
        (await db.Accounts.SingleAsync(x => x.Id == wallet, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(1000);
        (await db.Accounts.SingleAsync(x => x.SystemCode == "BANK_CLEARING" && x.Currency == "TRY", TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(-1000);
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Transfer_moves_money_between_wallets()
    {
        var (cs, from, to) = await SetupAsync();
        await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), from, Money.Create(1000, "TRY")));
        (await PostAsync(cs, Entries.Transfer(Ids.New(), Ids.New(), from, to, Money.Create(250, "TRY")))).ShouldBeOfType<PostingResult.Posted>();
        await using var db = OpenDb(cs);
        (await db.Accounts.SingleAsync(x => x.Id == from, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(750);
        (await db.Accounts.SingleAsync(x => x.Id == to, TestContext.Current.CancellationToken)).BalanceMinor.ShouldBe(250);
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Transfer_with_insufficient_funds_is_rejected_and_changes_nothing()
    {
        var (cs, from, to) = await SetupAsync();
        (await PostAsync(cs, Entries.Transfer(Ids.New(), Ids.New(), from, to, Money.Create(1, "TRY"))))
            .ShouldBeOfType<PostingResult.Rejected>().ReasonCode.ShouldBe("InsufficientFunds");
        await using var db = OpenDb(cs);
        (await db.JournalEntries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.Accounts.SingleAsync(x => x.Id == from, TestContext.Current.CancellationToken)).Version.ShouldBe(1);
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Posting_to_frozen_account_is_rejected_AccountFrozen()
    {
        var (cs, wallet, _) = await SetupAsync();
        await using (var db = OpenDb(cs))
        {
            var account = await db.Accounts.SingleAsync(x => x.Id == wallet, TestContext.Current.CancellationToken);
            account.Status = "Frozen";
            account.Version++;
            db.Events.Add(new LedgerEvent { StreamId = wallet, Version = account.Version, Type = "account-status-changed.v1", OccurredAt = DateTimeOffset.UtcNow,
                Data = EnvelopeSerializer.ToPayload(new { Status = "Frozen" }), Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = wallet }) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        (await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), wallet, Money.Create(1, "TRY"))))
            .ShouldBeOfType<PostingResult.Rejected>().ReasonCode.ShouldBe("AccountFrozen");
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Posting_with_wrong_currency_is_rejected_CurrencyMismatch()
    {
        var (cs, wallet, _) = await SetupAsync("USD");
        (await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), wallet, Money.Create(1, "TRY"))))
            .ShouldBeOfType<PostingResult.Rejected>().ReasonCode.ShouldBe("CurrencyMismatch");
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Posting_to_closed_account_is_rejected_AccountClosed()
    {
        var (cs, wallet, _) = await SetupAsync();
        await using (var db = OpenDb(cs))
        {
            var account = await db.Accounts.SingleAsync(x => x.Id == wallet, TestContext.Current.CancellationToken);
            account.Status = "Closed";
            account.Version++;
            db.Events.Add(new LedgerEvent { StreamId = wallet, Version = account.Version, Type = "account-status-changed.v1", OccurredAt = DateTimeOffset.UtcNow,
                Data = EnvelopeSerializer.ToPayload(new { Status = "Closed" }), Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = wallet }) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        (await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), wallet, Money.Create(1, "TRY"))))
            .ShouldBeOfType<PostingResult.Rejected>().ReasonCode.ShouldBe("AccountClosed");
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Posting_to_unknown_account_is_rejected_AccountNotFound()
    {
        var (cs, _, _) = await SetupAsync();
        (await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), Ids.New(), Money.Create(1, "TRY"))))
            .ShouldBeOfType<PostingResult.Rejected>().ReasonCode.ShouldBe("AccountNotFound");
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Posting_appends_one_event_per_line_with_next_version()
    {
        var (cs, wallet, _) = await SetupAsync();
        await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), wallet, Money.Create(10, "TRY")));
        await PostAsync(cs, Entries.TopUp(Ids.New(), Ids.New(), wallet, Money.Create(20, "TRY")));
        await using var db = OpenDb(cs);
        (await db.Events.Where(x => x.StreamId == wallet).OrderBy(x => x.Version).Select(x => x.Version).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe([1, 2, 3]);
        (await db.Events.CountAsync(x => x.Type == "ledger-entry-posted.v1", TestContext.Current.CancellationToken)).ShouldBe(4);
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    [Fact]
    public async Task Posting_writes_journal_entry_and_lines_with_balance_after()
    {
        var (cs, wallet, _) = await SetupAsync();
        var op = Ids.New();
        var payment = Ids.New();
        var posted = (await PostAsync(cs, Entries.TopUp(op, payment, wallet, Money.Create(100, "TRY")))).ShouldBeOfType<PostingResult.Posted>();
        await using var db = OpenDb(cs);
        var entry = await db.JournalEntries.SingleAsync(TestContext.Current.CancellationToken);
        entry.Id.ShouldBe(posted.JournalEntryId);
        entry.OperationId.ShouldBe(op);
        entry.PaymentId.ShouldBe(payment);
        entry.EntryType.ShouldBe("TopUp");
        entry.PostedAt.ShouldBe(posted.PostedAt);
        var lines = await db.JournalLines.OrderBy(x => x.LineNo).ToListAsync(TestContext.Current.CancellationToken);
        lines.Select(x => x.AmountMinor).ShouldBe([100L, 100L]);
        lines.Select(x => x.BalanceAfterMinor).ShouldBe([-100L, 100L]);
        lines.ShouldAllBe(x => x.EventGlobalSeq > 0);
        await LedgerAssert.InvariantsHoldAsync(cs);
    }

    private async Task<(string ConnectionString, Guid From, Guid To)> SetupAsync(string currency = "TRY")
    {
        var cs = await DatabaseFactory.CreateMigratedAsync<AccountsDbContext>(infrastructure, "posting", c => MigrateCommand.RunAsync(c, TestContext.Current.CancellationToken));
        await using var db = OpenDb(cs);
        var from = await TestAccounts.OpenWalletAsync(db, Ids.New(), currency);
        var to = await TestAccounts.OpenWalletAsync(db, Ids.New(), currency);
        return (cs, from, to);
    }

    private static AccountsDbContext OpenDb(string cs) => new(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(cs).Options);

    private static async Task<PostingResult> PostAsync(string cs, PostingRequest request)
    {
        await using var db = OpenDb(cs);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var result = await new PostingLedger(db, new EventStore(db), new FixedClock()).PostAsync(request, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    }
}
