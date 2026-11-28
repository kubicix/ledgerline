using System.Reflection;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Projections;

/// <summary>
/// One <c>account_statements</c> document per ledger line, replaced by <c>_id = "&lt;journalEntryId&gt;:&lt;lineNo&gt;"</c>
/// (reporting.md section 4). Every field comes from the record, so applying it again writes the same document.
/// </summary>
public sealed class AccountStatementProjection(MongoCollections collections) : IProjection
{
    private static readonly ReplaceOptions Upsert = new() { IsUpsert = true };

    public IReadOnlyCollection<string> MessageTypes { get; } =
        [typeof(JournalEntryLinePosted).GetCustomAttribute<MessageTypeAttribute>()!.Name];

    public async Task ApplyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var line = (JournalEntryLinePosted)EnvelopeSerializer.FromPayload(envelope.Payload, typeof(JournalEntryLinePosted));
        var document = AccountStatementDocument.From(line);
        await collections.AccountStatements.ReplaceOneAsync(
            Builders<AccountStatementDocument>.Filter.Eq(x => x.Id, document.Id), document, Upsert, ct);
    }
}

/// <summary>
/// A statement line as stored. IDs are lowercase hyphenated GUID strings (the Kafka key format), amounts 64-bit
/// integers, and <see cref="PostedAt"/> a UTC BSON date (millisecond precision).
/// </summary>
public sealed class AccountStatementDocument
{
    [BsonId] public required string Id { get; init; }
    [BsonElement("accountId")] public required string AccountId { get; init; }
    [BsonElement("journalEntryId")] public required string JournalEntryId { get; init; }
    [BsonElement("entryType")] public required string EntryType { get; init; }
    [BsonElement("direction")] public required string Direction { get; init; }
    [BsonElement("amountMinor")] public required long AmountMinor { get; init; }
    [BsonElement("currency")] public required string Currency { get; init; }
    [BsonElement("balanceAfterMinor")] public required long BalanceAfterMinor { get; init; }
    [BsonElement("paymentId")] public required string PaymentId { get; init; }
    [BsonElement("postedAt"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public required DateTime PostedAt { get; init; }

    /// <summary>
    /// Rejects a line without its identity or classification: deserialization leaves missing fields at their
    /// defaults, which would otherwise be stored as <c>"00000000-...:0"</c>.
    /// </summary>
    public static AccountStatementDocument From(JournalEntryLinePosted line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.JournalEntryId == Guid.Empty || line.AccountId == Guid.Empty || line.LineNo < 1 ||
            string.IsNullOrEmpty(line.EntryType) || string.IsNullOrEmpty(line.Direction) || string.IsNullOrEmpty(line.Currency))
            throw new InvalidDataException("The ledger line record lacks its entry, account, line number, type, direction or currency.");
        return new AccountStatementDocument
        {
            Id = $"{line.JournalEntryId}:{line.LineNo}",
            AccountId = line.AccountId.ToString(),
            JournalEntryId = line.JournalEntryId.ToString(),
            EntryType = line.EntryType,
            Direction = line.Direction,
            AmountMinor = line.AmountMinor,
            Currency = line.Currency,
            BalanceAfterMinor = line.BalanceAfterMinor,
            PaymentId = line.PaymentId.ToString(),
            PostedAt = line.PostedAt.UtcDateTime
        };
    }
}
