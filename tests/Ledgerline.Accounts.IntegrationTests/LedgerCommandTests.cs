using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.Outbox;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Ledgerline.Accounts.IntegrationTests;

public sealed class LedgerCommandTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task CreditTopUp_command_posts_entry_and_publishes_succeeded_and_journal_events()
    {
        await using var host = await StartAsync();
        var (wallet, _) = await WalletsAsync(host);
        await using var probe = await ProbeAsync(host);
        var op = Ids.New();
        var payment = Ids.New();
        var messageId = Ids.New();
        await probe.PublishAsync(new CreditTopUp(op, payment, wallet, 1000, "TRY"), payment, messageId);
        var reply = await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == op);
        reply.Kind.ShouldBe(LedgerOperationKind.CreditTopUp);
        var journal = await probe.WaitForAsync<JournalEntryPosted>(x => x.JournalEntryId == reply.JournalEntryId);
        journal.Lines.Single(x => x.AccountId == wallet).BalanceAfterMinor.ShouldBe(1000);
        var envelope = probe.Messages.Single(x => x.Type == "accounts.event.ledger-operation-succeeded.v1");
        envelope.CorrelationId.ShouldBe(payment);
        envelope.CausationId.ShouldBe(messageId);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task TransferFunds_with_insufficient_funds_publishes_rejected()
    {
        await using var host = await StartAsync();
        var (from, to) = await WalletsAsync(host);
        await using var probe = await ProbeAsync(host);
        var op = Ids.New();
        var payment = Ids.New();
        await probe.PublishAsync(new TransferFunds(op, payment, from, to, 1, "TRY"), payment);
        (await probe.WaitForAsync<LedgerOperationRejected>(x => x.OperationId == op)).ReasonCode.ShouldBe("InsufficientFunds");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await db.JournalEntries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await db.LedgerOperations.SingleAsync(TestContext.Current.CancellationToken)).Outcome.ShouldBe("Rejected");
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Same_operation_with_new_message_id_returns_same_reply_without_second_entry()
    {
        await using var host = await StartAsync();
        var (wallet, _) = await WalletsAsync(host);
        await using var probe = await ProbeAsync(host);
        var op = Ids.New();
        var payment = Ids.New();
        var command = new CreditTopUp(op, payment, wallet, 1000, "TRY");
        await probe.PublishAsync(command, payment);
        var first = await probe.WaitForAsync<LedgerOperationSucceeded>(x => x.OperationId == op);
        await probe.PublishAsync(command, payment);
        await Eventually.ShouldAsync(() => Task.FromResult(probe.Messages.Count(x => x.Type == "accounts.event.ledger-operation-succeeded.v1") == 2));
        var replies = probe.Messages.Where(x => x.Type == "accounts.event.ledger-operation-succeeded.v1")
            .Select(x => (LedgerOperationSucceeded)EnvelopeSerializer.FromPayload(x.Payload, typeof(LedgerOperationSucceeded))).ToArray();
        replies.ShouldAllBe(x => x == first);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await db.JournalEntries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.Type == "accounts.event.journal-entry-posted.v1", TestContext.Current.CancellationToken)).ShouldBe(1);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Same_message_id_twice_posts_once()
    {
        var acknowledgments = new CountingAck();
        await using var host = await StartAsync(acknowledgments);
        var (wallet, _) = await WalletsAsync(host);
        await using var probe = await ProbeAsync(host);
        var payment = Ids.New();
        var command = new CreditTopUp(Ids.New(), payment, wallet, 1000, "TRY");
        var messageId = Ids.New();
        await probe.PublishAsync(command, payment, messageId);
        await probe.PublishAsync(command, payment, messageId);
        await Eventually.ShouldAsync(() => Task.FromResult(acknowledgments.Count == 2));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await db.JournalEntries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.LedgerOperations.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.Set<OutboxMessage>().CountAsync(x => x.Type == "accounts.event.ledger-operation-succeeded.v1", TestContext.Current.CancellationToken)).ShouldBe(1);
        await LedgerAssert.InvariantsHoldAsync(host.ConnectionString);
    }

    [Fact]
    public async Task Separate_databases_do_not_share_command_deliveries()
    {
        await using var first = await StartAsync();
        await using var second = await StartAsync();
        var (wallet, _) = await WalletsAsync(first);
        await using var probe = await ProbeAsync(first);
        var payment = Ids.New();
        for (var i = 0; i < 20; i++) await probe.PublishAsync(new CreditTopUp(Ids.New(), payment, wallet, 1, "TRY"), payment);
        await Eventually.ShouldAsync(() => Task.FromResult(probe.Messages.Count(x => x.Type is "accounts.event.ledger-operation-succeeded.v1" or "accounts.event.ledger-operation-rejected.v1") == 20));
        await using var firstScope = first.Services.CreateAsyncScope();
        await using var secondScope = second.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        (await firstDb.JournalEntries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(20);
        (await secondDb.LedgerOperations.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        await LedgerAssert.InvariantsHoldAsync(first.ConnectionString);
        await LedgerAssert.InvariantsHoldAsync(second.ConnectionString);
    }

    private Task<ServiceHost<Program>> StartAsync(CountingAck? acknowledgments = null) =>
        ServiceHost<Program>.StartAsync(infrastructure, "command", services =>
        {
            services.AddSingleton<IClock, PreciseClock>();
            if (acknowledgments is not null) services.AddSingleton<IAckStrategy>(acknowledgments);
        });

    private static Task<BrokerProbe> ProbeAsync(ServiceHost<Program> host) =>
        BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "accounts.event.#");

    private static async Task<(Guid From, Guid To)> WalletsAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountsDbContext>();
        var first = await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY");
        var second = await TestAccounts.OpenWalletAsync(db, Ids.New(), "TRY");
        return (first, second);
    }

    public sealed class PreciseClock : IClock
    {
        public DateTimeOffset UtcNow => new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).AddTicks(9);
    }

    public sealed class CountingAck : IAckStrategy
    {
        private long count;
        public long Count => Interlocked.Read(ref count);
        public async Task AckAsync(IChannel channel, ulong deliveryTag, CancellationToken ct)
        {
            await channel.BasicAckAsync(deliveryTag, false, ct);
            Interlocked.Increment(ref count);
        }
    }
}
