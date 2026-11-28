using System.Reflection;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Accounts;
using Ledgerline.Reporting.Consuming;
using Ledgerline.Reporting.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Projections;

/// <summary>
/// <c>account_owners</c>: who owns each account, from <c>account-opened</c> events (reporting.md section 5). The API's
/// owner and party checks read only this collection; Reporting never calls Accounts. The document is replaced by
/// <c>_id = accountId</c> and holds the owner only: the event's contact email is personal data and is never stored.
/// </summary>
public sealed class AccountOwnersProjection(MongoCollections collections) : IProjection
{
    private static readonly ReplaceOptions Upsert = new() { IsUpsert = true };

    public IReadOnlyCollection<string> MessageTypes { get; } =
        [typeof(AccountOpened).GetCustomAttribute<MessageTypeAttribute>()!.Name];

    public async Task ApplyAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var opened = (AccountOpened)EnvelopeSerializer.FromPayload(envelope.Payload, typeof(AccountOpened));
        if (opened.AccountId == Guid.Empty) throw new InvalidDataException("The account-opened record has no account ID.");
        var document = new AccountOwnerDocument { Id = opened.AccountId.ToString(), OwnerId = opened.OwnerId?.ToString() };
        await collections.AccountOwners.ReplaceOneAsync(Builders<AccountOwnerDocument>.Filter.Eq(x => x.Id, document.Id), document, Upsert, ct);
    }
}

/// <summary>An account's owner, as lowercase hyphenated GUID strings. System accounts have no owner (null).</summary>
public sealed class AccountOwnerDocument
{
    [BsonId] public required string Id { get; init; }
    [BsonElement("ownerId")] public string? OwnerId { get; init; }
}
