using Ledgerline.Reporting.Projections;
using MongoDB.Driver;

namespace Ledgerline.Reporting.Infrastructure;

/// <summary>
/// The read model's collections (reporting.md sections 4 and 5) and their indexes. Every collection is opened through
/// <see cref="Open{TDocument}"/>, the one seam tests override to inject driver failures.
/// </summary>
public class MongoCollections(IMongoDatabase database)
{
    public const string AccountStatementsName = "account_statements";
    public const string PaymentTimelinesName = "payment_timelines";
    public const string MerchantDailyTotalsName = "merchant_daily_totals";
    public const string AccountOwnersName = "account_owners";

    /// <summary>Every collection of the read model; the rebuild drops all of them.</summary>
    public static IReadOnlyList<string> Names { get; } = [AccountStatementsName, PaymentTimelinesName, MerchantDailyTotalsName, AccountOwnersName];

    public const string DatabaseKey = "Reporting:Mongo:Database";
    public const string DefaultDatabase = "ledgerline_reporting";

    public IMongoDatabase Database { get; } = database;

    public IMongoCollection<AccountStatementDocument> AccountStatements => Open<AccountStatementDocument>(AccountStatementsName);
    public IMongoCollection<PaymentTimelineDocument> PaymentTimelines => Open<PaymentTimelineDocument>(PaymentTimelinesName);
    public IMongoCollection<MerchantDailyTotalsDocument> MerchantDailyTotals => Open<MerchantDailyTotalsDocument>(MerchantDailyTotalsName);
    public IMongoCollection<AccountOwnerDocument> AccountOwners => Open<AccountOwnerDocument>(AccountOwnersName);

    /// <summary>Creates the indexes of every collection. Idempotent: an existing identical index is left as it is.</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await AccountStatements.Indexes.CreateOneAsync(new CreateIndexModel<AccountStatementDocument>(
            Builders<AccountStatementDocument>.IndexKeys.Ascending(x => x.AccountId).Descending(x => x.PostedAt)),
            cancellationToken: ct);
        // Serves the daily totals query of one merchant over a range of days.
        await MerchantDailyTotals.Indexes.CreateOneAsync(new CreateIndexModel<MerchantDailyTotalsDocument>(
            Builders<MerchantDailyTotalsDocument>.IndexKeys.Ascending(x => x.MerchantAccountId).Ascending(x => x.Day)),
            cancellationToken: ct);
    }

    /// <summary>A client for <c>ConnectionStrings:Mongo</c>.</summary>
    public static MongoClient CreateClient(IConfiguration configuration) =>
        new(configuration.GetConnectionString("Mongo") ?? throw new InvalidOperationException("ConnectionStrings:Mongo is required."));

    /// <summary>The read model database, <c>Reporting:Mongo:Database</c> (default <c>ledgerline_reporting</c>).</summary>
    public static IMongoDatabase OpenDatabase(IMongoClient client, IConfiguration configuration) =>
        client.GetDatabase(configuration[DatabaseKey] ?? DefaultDatabase);

    protected virtual IMongoCollection<TDocument> Open<TDocument>(string name) => Database.GetCollection<TDocument>(name);
}
