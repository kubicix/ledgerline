using Ledgerline.Reporting.Projections;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Infrastructure;

/// <summary>
/// The read model's collections (reporting.md section 4) and their indexes. Every collection is opened through
/// <see cref="Open{TDocument}"/>, the one seam tests override to inject driver failures.
/// </summary>
public class MongoCollections(IMongoDatabase database)
{
    public const string AccountStatementsName = "account_statements";
    public const string PaymentTimelinesName = "payment_timelines";
    public const string MerchantDailyTotalsName = "merchant_daily_totals";

    public IMongoDatabase Database { get; } = database;

    public IMongoCollection<AccountStatementDocument> AccountStatements => Open<AccountStatementDocument>(AccountStatementsName);
    public IMongoCollection<PaymentTimelineDocument> PaymentTimelines => Open<PaymentTimelineDocument>(PaymentTimelinesName);
    public IMongoCollection<MerchantDailyTotalsDocument> MerchantDailyTotals => Open<MerchantDailyTotalsDocument>(MerchantDailyTotalsName);

    /// <summary>Creates the indexes of every collection. Idempotent: an existing identical index is left as it is.</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await AccountStatements.Indexes.CreateOneAsync(new CreateIndexModel<AccountStatementDocument>(
            Builders<AccountStatementDocument>.IndexKeys.Ascending(x => x.AccountId).Descending(x => x.PostedAt)),
            cancellationToken: ct);
    }

    protected virtual IMongoCollection<TDocument> Open<TDocument>(string name) => Database.GetCollection<TDocument>(name);
}
