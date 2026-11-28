using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Reporting.Infrastructure;
using Ledgerline.Reporting.Projections;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Features;

/// <summary>
/// <c>GET /payments/{paymentId}/timeline</c>: the timeline document, for a party of the payment or an admin. A party is
/// the payment's customer or the owner of its source or destination account, by Reporting's own
/// <c>account_owners</c>. An unknown payment and a caller who is not a party both get 404, as on Payments.
/// </summary>
internal static class TimelineEndpoint
{
    internal static async Task<IResult> HandleAsync(Guid paymentId, MongoCollections collections, ICurrentUser user, CancellationToken ct)
    {
        var id = paymentId.ToString();
        var timeline = await collections.PaymentTimelines.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (timeline is null || !await IsPartyAsync(timeline, collections, user, ct)) return ProblemResults.NotFound();
        return Results.Ok(TimelineResponse.From(timeline));
    }

    private static async Task<bool> IsPartyAsync(PaymentTimelineDocument timeline, MongoCollections collections, ICurrentUser user, CancellationToken ct)
    {
        var caller = user.Id.ToString();
        if (user.IsInRole("admin") || timeline.CustomerId == caller) return true;
        var accounts = new[] { timeline.SourceAccountId, timeline.DestinationAccountId }.OfType<string>().ToArray();
        return accounts.Length > 0 && await collections.AccountOwners
            .Find(Builders<AccountOwnerDocument>.Filter.In(x => x.Id, accounts) & Builders<AccountOwnerDocument>.Filter.Eq(x => x.OwnerId, caller))
            .AnyAsync(ct);
    }
}

/// <summary>
/// A payment timeline as stored (reporting.md section 4), with <c>_id</c> as <c>paymentId</c>. The summary fields are
/// null only while no event has been applied to the summary yet.
/// </summary>
public sealed record TimelineResponse(Guid PaymentId, string? Kind, string? Status, long AmountMinor, string? Currency,
    Guid? CustomerId, Guid? SourceAccountId, Guid? DestinationAccountId, long CapturedMinor, long RefundedMinor,
    IReadOnlyList<TimelineEventResponse> Events, DateTimeOffset? UpdatedAt)
{
    internal static TimelineResponse From(PaymentTimelineDocument timeline) => new(Guid.Parse(timeline.Id), timeline.Kind, timeline.Status,
        timeline.AmountMinor, timeline.Currency, ParseOrNull(timeline.CustomerId), ParseOrNull(timeline.SourceAccountId),
        ParseOrNull(timeline.DestinationAccountId), timeline.CapturedMinor, timeline.RefundedMinor,
        timeline.Events.Select(x => new TimelineEventResponse(Guid.Parse(x.MessageId), x.Type, StatementEndpoint.Utc(x.At))).ToArray(),
        timeline.UpdatedAt is { } updatedAt ? StatementEndpoint.Utc(updatedAt) : null);

    private static Guid? ParseOrNull(string? id) => id is null ? null : Guid.Parse(id);
}

/// <summary>One event of a timeline: its message ID, short type name and <c>occurredAt</c>.</summary>
public sealed record TimelineEventResponse(Guid MessageId, string Type, DateTimeOffset At);
