using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.BuildingBlocks.Messaging.Consuming;
using Ledgerline.BuildingBlocks.Messaging.RabbitMq;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Contracts.Reconciliation;
using Ledgerline.Notifications.Features.Reconciliation;
using Ledgerline.Notifications.Infrastructure;
using Ledgerline.Notifications.Infrastructure.Entities;
using Ledgerline.Notifications.Sending;
using Ledgerline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(InfrastructureFixture))]

namespace Ledgerline.Notifications.IntegrationTests;

public sealed class NotificationFlowTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task PaymentCompleted_creates_one_notification_for_customer()
    {
        await using var scenario = await StartAsync();
        await scenario.PublishAsync(Completed(scenario.Account, scenario.Owner));
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Sent" }]);
        scenario.Sender.Sent.Count.ShouldBe(1);
        scenario.Sender.Sent.Single().Subject.ShouldContain("1,500.00 TRY");
    }

    [Fact]
    public async Task Transfer_completed_notifies_sender_and_receiver()
    {
        await using var scenario = await StartAsync();
        var receiver = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new AccountOpened(receiver, Guid.NewGuid(), "Wallet", "TRY", "receiver@example.test", DateTimeOffset.UtcNow), Guid.NewGuid());
        await scenario.PublishAsync(Completed(receiver, scenario.Owner) with { Kind = "Transfer", SourceAccountId = scenario.Account });
        await WaitAsync(scenario.Host, rows => rows.Length == 2 && rows.All(x => x.Status == "Sent"));
        scenario.Sender.Sent.Select(x => x.Template).Order().ShouldBe(["payment-completed", "transfer-received"]);
    }

    [Fact]
    public async Task Same_event_twice_creates_one_notification()
    {
        await using var scenario = await StartAsync();
        var message = Completed(scenario.Account, scenario.Owner);
        var id = Guid.NewGuid();
        await scenario.Probe.PublishAsync(message, message.PaymentId, id);
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Sent" }]);
        await scenario.Probe.PublishAsync(message, message.PaymentId, id);
        await scenario.Probe.PublishAsync(message, message.PaymentId, id);
        await Eventually.ShouldAsync(async () => (await RowsAsync(scenario.Host)).Length == 1);
        scenario.Sender.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_recipient_is_retried_then_stored_as_Skipped()
    {
        await using var scenario = await StartAsync();
        await scenario.PublishAsync(Completed(Guid.NewGuid(), Guid.NewGuid()));
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Skipped", Reason: "RecipientUnknown" }]);
        scenario.Sender.Sent.ShouldBeEmpty();
        await using var scope = scenario.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        (await db.Set<Ledgerline.BuildingBlocks.Messaging.Inbox.InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Smtp_failure_retries_with_backoff_then_Failed_after_6_attempts()
    {
        await using var scenario = await StartAsync(fail: true);
        await scenario.PublishAsync(Completed(scenario.Account, scenario.Owner));
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Failed", Attempts: 6 }]);
        var times = scenario.Sender.AttemptTimes.ToArray();
        times.Length.ShouldBe(6);
        (times[5] - times[4]).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Sender_failure_does_not_block_consumer()
    {
        await using var scenario = await StartAsync(fail: true);
        for (var i = 0; i < 10; i++) await scenario.PublishAsync(Completed(scenario.Account, scenario.Owner));
        await WaitAsync(scenario.Host, rows => rows.Length == 10);
        (await RowsAsync(scenario.Host)).ShouldAllBe(x => x.Status != "Sent");
    }

    [Fact]
    public async Task Email_address_never_appears_in_logs()
    {
        var logs = new LogCapture();
        await using var scenario = await StartAsync(fail: true, extra: services => services.AddLogging(builder => builder.AddProvider(logs)));
        await scenario.PublishAsync(Completed(scenario.Account, scenario.Owner));
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Failed" }]);
        string.Join('\n', logs.Messages).ShouldNotContain("@example.test");
    }

    [Fact]
    public async Task Email_reaches_Mailpit()
    {
        await using var mailpit = new ContainerBuilder("axllent/mailpit:v1.33.1")
            .WithPortBinding(1025, true).WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1025)).Build();
        await mailpit.StartAsync(TestContext.Current.CancellationToken);
        await using var scenario = await StartAsync(extra: services =>
        {
            services.AddSingleton(new SmtpOptions { Host = mailpit.Hostname, Port = mailpit.GetMappedPublicPort(1025) });
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        });
        await scenario.PublishAsync(Completed(scenario.Account, scenario.Owner));
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Sent" }]);
        using var client = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/messages", TestContext.Current.CancellationToken);
        response.GetProperty("total").GetInt32().ShouldBe(1);
        response.GetProperty("messages")[0].GetProperty("Subject").GetString()!.ShouldContain("1,500.00 TRY");
    }

    [Fact]
    public async Task List_returns_only_own_notifications()
    {
        await using var scenario = await StartAsync();
        for (var i = 0; i < 3; i++) await scenario.PublishAsync(Completed(scenario.Account, scenario.Owner));
        await WaitAsync(scenario.Host, rows => rows.Length == 3);
        using var own = scenario.Host.CreateClient(scenario.Owner);
        var first = await own.GetFromJsonAsync<JsonElement>("/notifications?limit=2", TestContext.Current.CancellationToken);
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await own.GetFromJsonAsync<JsonElement>("/notifications?limit=2&cursor=" + cursor, TestContext.Current.CancellationToken);
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        using var other = scenario.Host.CreateClient(Guid.NewGuid());
        var empty = await other.GetFromJsonAsync<JsonElement>("/notifications", TestContext.Current.CancellationToken);
        empty.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Mismatch_event_emails_operations_address()
    {
        await using var scenario = await StartAsync(settings: OperationsAddress("operations@example.test"));
        var run = Guid.NewGuid();
        var payment = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new ReconciliationMismatchDetected(run, new DateOnly(2026, 11, 13), "BankCaptureNotInLedger",
            payment.ToString(), 150000, null, "TRY"), run);
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Sent" }]);
        (await RowsAsync(scenario.Host)).Length.ShouldBe(1);
        var sent = scenario.Sender.Sent.Single();
        sent.RecipientEmail.ShouldBe("operations@example.test");
        sent.RecipientOwnerId.ShouldBe(MismatchDetectedHandler.OperationsOwnerId);
        sent.Template.ShouldBe("reconciliation-mismatch");
        sent.PaymentId.ShouldBe(payment);
        sent.Subject.ShouldBe("Reconciliation mismatch on 2026-11-13: BankCaptureNotInLedger");
        sent.Body.ShouldContain("1,500.00 TRY");
        sent.Body.ShouldContain(run.ToString());
    }

    [Fact]
    public async Task Mismatch_event_twice_creates_one_notification()
    {
        await using var scenario = await StartAsync(settings: OperationsAddress("operations@example.test"));
        var mismatch = new ReconciliationMismatchDetected(Guid.NewGuid(), new DateOnly(2026, 11, 13), "AmountMismatch", Guid.NewGuid().ToString(), 1001, 1000, "TRY");
        var id = Guid.NewGuid();
        await scenario.Probe.PublishAsync(mismatch, mismatch.RunId, id);
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Sent" }]);
        await scenario.Probe.PublishAsync(mismatch, mismatch.RunId, id);
        await scenario.Probe.PublishAsync(mismatch, mismatch.RunId, id);
        await Eventually.ShouldAsync(async () => (await RowsAsync(scenario.Host)).Length == 1);
        scenario.Sender.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Mismatch_of_a_currency_reference_has_no_payment_and_renders_a_negative_amount()
    {
        await using var scenario = await StartAsync(settings: OperationsAddress("operations@example.test"));
        var run = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new ReconciliationMismatchDetected(run, new DateOnly(2026, 11, 13), "LedgerImbalance", "TRY", null, -50, "TRY"), run);
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Sent" }]);
        var sent = scenario.Sender.Sent.Single();
        sent.PaymentId.ShouldBeNull();
        sent.Body.ShouldContain("Bank amount: none");
        sent.Body.ShouldContain("Ledger amount: -0.50 TRY");
    }

    [Fact]
    public async Task Mismatch_event_without_operations_address_is_retried_then_stored_as_Skipped()
    {
        await using var scenario = await StartAsync();
        var run = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new ReconciliationMismatchDetected(run, new DateOnly(2026, 11, 13), "AmountMismatch",
            Guid.NewGuid().ToString(), 1001, 1000, "TRY"), run);
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Skipped", Reason: "RecipientUnknown", Template: "reconciliation-mismatch" }]);
        scenario.Sender.Sent.ShouldBeEmpty();
        await using var scope = scenario.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        (await db.Set<Ledgerline.BuildingBlocks.Messaging.Inbox.InboxMessage>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Operations_address_never_appears_in_logs()
    {
        var logs = new LogCapture();
        await using var scenario = await StartAsync(fail: true, settings: OperationsAddress("operations@example.test"),
            extra: services => services.AddLogging(builder => builder.AddProvider(logs)));
        var run = Guid.NewGuid();
        await scenario.Probe.PublishAsync(new ReconciliationMismatchDetected(run, new DateOnly(2026, 11, 13), "AmountMismatch",
            Guid.NewGuid().ToString(), 1001, 1000, "TRY"), run);
        await WaitAsync(scenario.Host, rows => rows is [{ Status: "Failed" }]);
        string.Join('\n', logs.Messages).ShouldNotContain("@example.test");
    }

    [Fact]
    public async Task Notification_service_uses_inbox_without_outbox()
    {
        await using var scenario = await StartAsync();
        await using var scope = scenario.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.Model.FindEntityType(typeof(Ledgerline.BuildingBlocks.Messaging.Outbox.OutboxMessage)).ShouldBeNull();
    }

    private async Task<Scenario> StartAsync(bool fail = false, Action<IServiceCollection>? extra = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var sender = new RecordingSender(fail);
        var host = await ServiceHost<Program>.StartAsync(infrastructure, "notifications", services =>
        {
            services.AddSingleton<IEmailSender>(sender);
            services.AddSingleton(new SenderOptions { PollIntervalMs = 10, BackoffUnit = TimeSpan.FromMilliseconds(10) });
            services.AddSingleton(new ConsumerOptions { Service = "notifications", RetryTtlMs = 25, MaxAttempts = 3 });
            extra?.Invoke(services);
        }, settings);
        var probe = await BrokerProbe.CreateAsync(host.Services.GetRequiredService<RabbitMqOptions>().Uri, "payments.event.#");
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await probe.PublishAsync(new AccountOpened(account, owner, "Wallet", "TRY", "owner@example.test", DateTimeOffset.UtcNow), account);
        await Eventually.ShouldAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Recipients.AnyAsync();
        });
        return new(host, probe, sender, owner, account);
    }

    private static Dictionary<string, string?> OperationsAddress(string address) => new() { ["Notifications:OperationsEmail"] = address };
    private static PaymentCompleted Completed(Guid account, Guid owner) => new(Guid.NewGuid(), "TopUp", owner, 150000, "TRY", null, account, DateTimeOffset.UtcNow, "capture");
    private static async Task<Notification[]> RowsAsync(ServiceHost<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Notifications.AsNoTracking().ToArrayAsync();
    }
    private static Task WaitAsync(ServiceHost<Program> host, Func<Notification[], bool> predicate) =>
        Eventually.ShouldAsync(async () => predicate(await RowsAsync(host)), TimeSpan.FromSeconds(15));

    private sealed record Scenario(ServiceHost<Program> Host, BrokerProbe Probe, RecordingSender Sender, Guid Owner, Guid Account) : IAsyncDisposable
    {
        internal Task PublishAsync(PaymentCompleted message) => Probe.PublishAsync(message, message.PaymentId);
        public async ValueTask DisposeAsync() { await Probe.DisposeAsync(); await Host.DisposeAsync(); }
    }
    private sealed class RecordingSender(bool fail) : IEmailSender
    {
        internal ConcurrentQueue<Notification> Sent { get; } = new();
        internal ConcurrentQueue<DateTimeOffset> AttemptTimes { get; } = new();
        public Task SendAsync(Notification notification, CancellationToken ct)
        {
            AttemptTimes.Enqueue(DateTimeOffset.UtcNow);
            if (fail) throw new InvalidOperationException("SMTP rejected " + notification.RecipientEmail);
            Sent.Enqueue(notification);
            return Task.CompletedTask;
        }
    }
    private sealed class LogCapture : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + exception?.ToString());
        }
    }
}
