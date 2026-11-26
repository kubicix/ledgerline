using Ledgerline.Testing;
using Npgsql;

namespace Ledgerline.SystemTests.Harness;

/// <summary>
/// Money invariants of a system test run. Global checks: trial balance zero per currency (admin API), no payment or
/// refund in flight anywhere (payments_db), and the event-replay invariants of <see cref="LedgerAssert"/>. Run checks:
/// every payment and refund the run created is final, and per currency the sum over the run's wallets of
/// ((balance + held) now minus (balance + held) at run start) equals the sum of the run's Completed top-ups.
/// </summary>
public static class SystemInvariants
{
    // Compose publishes Postgres on localhost:5432; local development credentials from deploy/compose/postgres.
    public const string AccountsDatabase = "Host=localhost;Port=5432;Database=accounts_db;Username=accounts;Password=accounts_dev";
    public const string PaymentsDatabase = "Host=localhost;Port=5432;Database=payments_db;Username=payments;Password=payments_dev";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public static async Task AssertAsync(LedgerlineClient client, SystemRun run, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var finals = await WaitUntilRunIsFinalAsync(client, run, timeout ?? DefaultTimeout, ct);
        await WaitUntilNothingInFlightAsync(timeout ?? DefaultTimeout, ct);
        await AssertTrialBalanceZeroAsync(client, ct);
        await AssertWalletDeltasEqualCompletedTopUpsAsync(client, run, finals, ct);
        await LedgerAssert.InvariantsHoldAsync(AccountsDatabase);
    }

    /// <summary>Waits until every payment and refund of the run is final; returns the final status per id.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> WaitUntilRunIsFinalAsync(LedgerlineClient client, SystemRun run, TimeSpan timeout, CancellationToken ct = default)
    {
        var statuses = new Dictionary<Guid, string>();
        var pending = new Dictionary<Guid, string>();
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                foreach (var operation in run.Operations.Where(x => !statuses.ContainsKey(x.Id)))
                {
                    var status = operation.Kind == "Refund"
                        ? (await client.ListRefundsAsync(KeycloakTokens.Admin, operation.PaymentId!.Value, ct)).Items.Single(x => x.Id == operation.Id).Status
                        : (await client.GetPaymentAsync(KeycloakTokens.Admin, operation.Id, ct)).Status;
                    if (IsFinal(operation.Kind, status))
                    {
                        statuses[operation.Id] = status;
                        pending.Remove(operation.Id);
                    }
                    else pending[operation.Id] = $"{operation.Kind} {operation.Id} {status}";
                }
                return statuses.Count == run.Operations.Count;
            }, timeout);
        }
        catch (ShouldAssertException error)
        {
            throw new ShouldAssertException($"{pending.Count} run operations are not final, for example: " +
                $"{string.Join("; ", pending.Values.Take(5))}. {error.Message}", error);
        }
        return statuses;
    }

    /// <summary>Global: no payment is Processing, Capturing, Voiding or Compensating and no refund is Processing.</summary>
    public static async Task WaitUntilNothingInFlightAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        long inFlight = -1;
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(PaymentsDatabase);
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM payments WHERE status = ANY(@statuses))
                         + (SELECT count(*) FROM refunds WHERE status = 'Processing')
                    """, connection);
                command.Parameters.AddWithValue("statuses", LedgerlineClient.InFlightPaymentStatuses.ToArray());
                inFlight = (long)(await command.ExecuteScalarAsync(ct))!;
                return inFlight == 0;
            }, timeout);
        }
        catch (ShouldAssertException error)
        {
            throw new ShouldAssertException($"{inFlight} payments or refunds are still in flight. {error.Message}", error);
        }
    }

    public static async Task AssertTrialBalanceZeroAsync(LedgerlineClient client, CancellationToken ct = default)
    {
        var trial = await client.GetTrialBalanceAsync(ct);
        trial.Currencies.ShouldNotBeEmpty();
        foreach (var currency in trial.Currencies)
            currency.SumOfBalancesMinor.ShouldBe(0, $"Trial balance for {currency.Currency} must be zero.");
    }

    public static async Task AssertWalletDeltasEqualCompletedTopUpsAsync(LedgerlineClient client, SystemRun run,
        IReadOnlyDictionary<Guid, string> finals, CancellationToken ct = default)
    {
        var deltas = await run.WalletDeltasAsync(client, ct);
        var currencies = run.Wallets.Select(x => x.Currency).Distinct(StringComparer.Ordinal);
        foreach (var currency in currencies)
        {
            var walletSum = run.Wallets.Where(x => x.Currency == currency).Sum(x => deltas[x.AccountId]);
            var topUps = run.Operations.Where(x => x.Kind == "TopUp" && x.Currency == currency && finals[x.Id] == "Completed").Sum(x => x.AmountMinor);
            walletSum.ShouldBe(topUps, $"Sum of {currency} wallet deltas must equal this run's completed top-ups.");
        }
    }

    // Authorized is a resting state (waits for capture, void or expiry) but not final for a run's own payment.
    private static bool IsFinal(string kind, string status) => kind == "Refund"
        ? status != "Processing"
        : status != "Authorized" && !LedgerlineClient.InFlightPaymentStatuses.Contains(status);
}
