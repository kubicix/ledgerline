using System.Reflection;
using Confluent.Kafka;
using Ledgerline.Accounts.Infrastructure;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Contracts.Payments;
using Ledgerline.Reconciliation.Staging;
using Ledgerline.Testing;
using Npgsql;

namespace Ledgerline.Reconciliation.IntegrationTests;

/// <summary>
/// The two staging consumers (reconciliation.md sections 2 and 6), driven through real Kafka records into the
/// service's own PostgreSQL database. Every assertion runs after the last produced record was committed, so a late or
/// duplicate record that changed nothing has really been consumed.
/// </summary>
public sealed class StagingTests(InfrastructureFixture infrastructure)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Payment_events_upsert_staged_payment_with_latest_status()
    {
        await using var host = await StagingHost.StartAsync(infrastructure);
        var topUp = Guid.NewGuid();
        var authorized = Wrap(new PaymentAuthorized(topUp, "TopUp", Guid.NewGuid(), 5000, "TRY", null, Guid.NewGuid(), T0, T0.AddDays(7)));
        var completed = Wrap(new PaymentCompleted(topUp, "TopUp", Guid.NewGuid(), 5000, "TRY", null, Guid.NewGuid(), T0.AddMinutes(2), "cap-1"));
        // Older than the completion, produced after it: it must not overwrite the newer status.
        var lateCaptured = Wrap(new PaymentCaptured(topUp, "TopUp", Guid.NewGuid(), 5000, "TRY", null, Guid.NewGuid(), T0.AddMinutes(1), 5000));
        var failedPayment = Guid.NewGuid();
        var failedAuthorized = Wrap(new PaymentAuthorized(failedPayment, "Transfer", Guid.NewGuid(), 700, "EUR", Guid.NewGuid(), Guid.NewGuid(), T0, T0.AddDays(7)));
        var failed = Wrap(new PaymentFailed(failedPayment, "Transfer", Guid.NewGuid(), 700, "EUR", Guid.NewGuid(), Guid.NewGuid(), T0.AddMinutes(1), "ManualReview", "bank-timeout"));
        var refundedPayment = Guid.NewGuid();
        var merchantCompleted = Wrap(new PaymentCompleted(refundedPayment, "MerchantPayment", Guid.NewGuid(), 9000, "USD", Guid.NewGuid(), Guid.NewGuid(), T0, "cap-m"));
        // A refund is acknowledged and not staged; a later event without a capture id keeps the one already staged.
        var refunded = Wrap(new PaymentRefunded(refundedPayment, "MerchantPayment", Guid.NewGuid(), 9000, "USD", Guid.NewGuid(), Guid.NewGuid(), T0.AddMinutes(3), Guid.NewGuid(), 1000, 1000));
        var voided = Wrap(new PaymentVoided(refundedPayment, "MerchantPayment", Guid.NewGuid(), 9000, "USD", Guid.NewGuid(), Guid.NewGuid(), T0.AddMinutes(4), "test"));

        var delivered = new List<DeliveryResult<string, byte[]>>();
        foreach (var record in new[] { authorized, completed, lateCaptured })
            delivered.Add(await host.ProduceAsync(host.Options.PaymentEventsTopic, topUp, record.Value));
        foreach (var record in new[] { failedAuthorized, failed })
            delivered.Add(await host.ProduceAsync(host.Options.PaymentEventsTopic, failedPayment, record.Value));
        foreach (var record in new[] { merchantCompleted, refunded, voided })
            delivered.Add(await host.ProduceAsync(host.Options.PaymentEventsTopic, refundedPayment, record.Value));
        await host.WaitForCommitAsync(delivered);

        (await PaymentsAsync(host)).ShouldBe([
            Row(topUp, "TopUp", "Completed", 5000, "TRY", "cap-1", T0.AddMinutes(2), completed.Id),
            Row(failedPayment, "Transfer", "ManualReview", 700, "EUR", null, T0.AddMinutes(1), failed.Id),
            Row(refundedPayment, "MerchantPayment", "Voided", 9000, "USD", "cap-m", T0.AddMinutes(4), voided.Id)
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Ledger_lines_are_staged_once_with_bank_clearing_flag()
    {
        await using var host = await StagingHost.StartAsync(infrastructure);
        var entry = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var wallet = Guid.NewGuid();
        var clearing = SystemAccounts.IdFor(SystemAccounts.BankClearing, "TRY");
        var debit = Wrap(new JournalEntryLinePosted(entry, "TopUp", payment, "TRY", T0, clearing, "Debit", 5000, 5000, 1));
        var credit = Wrap(new JournalEntryLinePosted(entry, "TopUp", payment, "TRY", T0, wallet, "Credit", 5000, 5000, 2));

        var delivered = new List<DeliveryResult<string, byte[]>>
        {
            await host.ProduceAsync(host.Options.LedgerEntriesTopic, clearing, debit.Value),
            await host.ProduceAsync(host.Options.LedgerEntriesTopic, wallet, credit.Value),
            // The second delivery of the same record.
            await host.ProduceAsync(host.Options.LedgerEntriesTopic, clearing, debit.Value)
        };
        await host.WaitForCommitAsync(delivered);

        (await LedgerLinesAsync(host)).ShouldBe([
            Line(entry, 1, clearing, true, "TopUp", "Debit", 5000, "TRY", payment, T0),
            Line(entry, 2, wallet, false, "TopUp", "Credit", 5000, "TRY", payment, T0)
        ], ignoreOrder: true);

        // The progress Task 9 waits on: the partitions the consumer owns, and the last committed record of each.
        host.Progress.OwnedPartitions.ShouldBe(host.AllPartitions, ignoreOrder: true);
        foreach (var last in delivered.GroupBy(x => x.TopicPartition).Select(x => x.MaxBy(record => record.Offset.Value)!))
            host.Progress.LastCommitted(last.TopicPartition).ShouldBe(
                new PartitionProgress(last.Offset.Value, new DateTimeOffset(last.Timestamp.UtcDateTime, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Duplicate_records_do_not_duplicate_rows()
    {
        await using var host = await StagingHost.StartAsync(infrastructure);
        var payment = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var wallet = Guid.NewGuid();
        var authorized = Wrap(new PaymentAuthorized(payment, "TopUp", customer, 1200, "TRY", null, wallet, T0, T0.AddDays(7)));
        var completed = Wrap(new PaymentCompleted(payment, "TopUp", customer, 1200, "TRY", null, wallet, T0.AddMinutes(1), "cap-d"));
        var entry = Guid.NewGuid();
        var clearing = SystemAccounts.IdFor(SystemAccounts.BankClearing, "TRY");
        var debit = Wrap(new JournalEntryLinePosted(entry, "TopUp", payment, "TRY", T0.AddMinutes(1), clearing, "Debit", 1200, 1200, 1));
        var credit = Wrap(new JournalEntryLinePosted(entry, "TopUp", payment, "TRY", T0.AddMinutes(1), wallet, "Credit", 1200, 1200, 2));

        var delivered = new List<DeliveryResult<string, byte[]>>();
        for (var round = 0; round < 2; round++)
        {
            foreach (var record in new[] { authorized, completed })
                delivered.Add(await host.ProduceAsync(host.Options.PaymentEventsTopic, payment, record.Value));
            delivered.Add(await host.ProduceAsync(host.Options.LedgerEntriesTopic, clearing, debit.Value));
            delivered.Add(await host.ProduceAsync(host.Options.LedgerEntriesTopic, wallet, credit.Value));
        }
        await host.WaitForCommitAsync(delivered);

        (await PaymentsAsync(host)).ShouldBe([Row(payment, "TopUp", "Completed", 1200, "TRY", "cap-d", T0.AddMinutes(1), completed.Id)]);
        (await LedgerLinesAsync(host)).ShouldBe([
            Line(entry, 1, clearing, true, "TopUp", "Debit", 1200, "TRY", payment, T0.AddMinutes(1)),
            Line(entry, 2, wallet, false, "TopUp", "Credit", 1200, "TRY", payment, T0.AddMinutes(1))
        ], ignoreOrder: true);
    }

    private sealed record Record(Guid Id, byte[] Value);

    private static Record Wrap<T>(T payload) where T : notnull
    {
        var type = typeof(T).GetCustomAttribute<MessageTypeAttribute>()!.Name;
        // The envelope time is the produce time on purpose: staging must order by the event's own time.
        var envelope = new MessageEnvelope(Guid.NewGuid(), type, DateTimeOffset.UtcNow, Guid.NewGuid(), null, null, EnvelopeSerializer.ToPayload(payload));
        return new Record(envelope.MessageId, EnvelopeSerializer.Serialize(envelope));
    }

    private static string Row(Guid paymentId, string kind, string status, long amountMinor, string currency, string? bankCaptureId,
        DateTimeOffset occurredAt, Guid lastMessageId) =>
        $"{paymentId} {kind} {status} {amountMinor} {currency} {bankCaptureId ?? "null"} {occurredAt.UtcDateTime:O} {lastMessageId}";

    private static string Line(Guid journalEntryId, int lineNo, Guid accountId, bool isBankClearing, string entryType, string direction,
        long amountMinor, string currency, Guid paymentId, DateTimeOffset postedAt) =>
        $"{journalEntryId} {lineNo} {accountId} {isBankClearing} {entryType} {direction} {amountMinor} {currency} {paymentId} {postedAt.UtcDateTime:O}";

    private static async Task<List<string>> PaymentsAsync(StagingHost host)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT payment_id, kind, status, amount_minor, currency, bank_capture_id, occurred_at, last_message_id FROM staged_payments
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(Row(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetGuid(7)));
        return rows;
    }

    private static async Task<List<string>> LedgerLinesAsync(StagingHost host)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT journal_entry_id, line_no, account_id, is_bank_clearing, entry_type, direction, amount_minor, currency, payment_id, posted_at
            FROM staged_ledger_lines
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(Line(reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetBoolean(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetGuid(8), reader.GetFieldValue<DateTimeOffset>(9)));
        return rows;
    }
}
