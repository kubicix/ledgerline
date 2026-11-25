using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledgerline.Testing;

namespace Ledgerline.SystemTests.Harness;

public sealed record AccountDto(Guid Id, Guid? OwnerId, string Type, string Currency, string Status, long BalanceMinor, long HeldMinor, int Version);

public sealed record PaymentDto(Guid Id, string Kind, string Status, long AmountMinor, string Currency, Guid? SourceAccountId,
    Guid DestinationAccountId, string? CaptureMode, long CapturedMinor, long RefundedMinor, string? FailureReason);

public sealed record RefundDto(Guid Id, Guid PaymentId, long AmountMinor, string Status, string? FailureReason);

public sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record TrialBalanceCurrencyDto(string Currency, long SumOfBalancesMinor, int Accounts, int JournalEntries);

public sealed record TrialBalanceDto(DateTimeOffset AsOf, IReadOnlyList<TrialBalanceCurrencyDto> Currencies);

/// <summary>
/// Typed calls through the YARP gateway with real Keycloak tokens. Endpoints and payloads mirror scripts/smoke.sh.
/// Every POST sends an Idempotency-Key (a fresh GUID-based key unless the caller passes one) and is retried with the
/// same key while the gateway answers 404 (account replica not yet in Payments), 409 request-in-progress, 429 or 502 to
/// 504. Every request also retries connection failures, HttpClient timeouts and Keycloak 5xx answers.
/// </summary>
public sealed class LedgerlineClient(HttpClient http, KeycloakTokens tokens) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpStatusCode[] Transient =
        [HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

    public static readonly IReadOnlySet<string> InFlightPaymentStatuses = new HashSet<string>(StringComparer.Ordinal) { "Processing", "Capturing", "Voiding", "Compensating" };

    public KeycloakTokens Tokens => tokens;

    /// <summary>How long one call keeps retrying transient answers. Above the 30 s Redis in-flight lease on purpose.</summary>
    public TimeSpan RetryWindow { get; init; } = TimeSpan.FromSeconds(60);

    public static LedgerlineClient CreateDefault()
    {
        var gateway = new Uri(Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://localhost:8080");
        var http = new HttpClient { BaseAddress = gateway, Timeout = TimeSpan.FromSeconds(30) };
        return new LedgerlineClient(http, new KeycloakTokens(http, KeycloakTokens.DefaultRealmUrl));
    }

    public static string NewKey(string purpose) => $"systest-{purpose}-{Guid.NewGuid():N}";

    public Task<IReadOnlyList<AccountDto>> ListAccountsAsync(string user, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AccountDto>>(user, "/api/accounts", ct);

    public Task<AccountDto> GetAccountAsync(string user, Guid accountId, CancellationToken ct = default) =>
        GetAsync<AccountDto>(user, $"/api/accounts/{accountId}", ct);

    /// <summary>Returns the caller's wallet of this type and currency, opening it when missing (one per type and currency).</summary>
    public async Task<AccountDto> EnsureWalletAsync(string user, string type, string currency, CancellationToken ct = default)
    {
        var existing = (await ListAccountsAsync(user, ct)).SingleOrDefault(a => a.Type == type && a.Currency == currency);
        return existing ?? await PostAsync<AccountDto>(user, "/api/accounts", new { type, currency }, NewKey("wallet"), ct);
    }

    public Task<PaymentDto> TopUpAsync(string user, Guid accountId, long amountMinor, string currency, string cardToken = "tok_ok",
        string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<PaymentDto>(user, "/api/topups", new { accountId, amountMinor, currency, cardToken }, idempotencyKey ?? NewKey("topup"), ct);

    public Task<PaymentDto> TransferAsync(string user, Guid fromAccountId, Guid toAccountId, long amountMinor, string currency,
        string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<PaymentDto>(user, "/api/transfers", new { fromAccountId, toAccountId, amountMinor, currency }, idempotencyKey ?? NewKey("transfer"), ct);

    public Task<PaymentDto> CreateMerchantPaymentAsync(string user, Guid customerAccountId, Guid merchantAccountId, long amountMinor,
        string currency, string captureMode = "Manual", string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<PaymentDto>(user, "/api/payments", new { customerAccountId, merchantAccountId, amountMinor, currency, captureMode },
            idempotencyKey ?? NewKey("merchant"), ct);

    public Task<PaymentDto> CaptureAsync(string user, Guid paymentId, long? amountMinor = null, string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<PaymentDto>(user, $"/api/payments/{paymentId}/capture", new { amountMinor }, idempotencyKey ?? NewKey("capture"), ct);

    public Task<RefundDto> RefundAsync(string user, Guid paymentId, long amountMinor, string? idempotencyKey = null, CancellationToken ct = default) =>
        PostAsync<RefundDto>(user, $"/api/payments/{paymentId}/refunds", new { amountMinor }, idempotencyKey ?? NewKey("refund"), ct);

    public Task<PaymentDto> GetPaymentAsync(string user, Guid paymentId, CancellationToken ct = default) =>
        GetAsync<PaymentDto>(user, $"/api/payments/{paymentId}", ct);

    public Task<PageDto<RefundDto>> ListRefundsAsync(string user, Guid paymentId, CancellationToken ct = default) =>
        GetAsync<PageDto<RefundDto>>(user, $"/api/payments/{paymentId}/refunds?limit=100", ct);

    /// <summary>Admin API: <c>GET /api/ledger/trial-balance</c>.</summary>
    public Task<TrialBalanceDto> GetTrialBalanceAsync(CancellationToken ct = default) =>
        GetAsync<TrialBalanceDto>(KeycloakTokens.Admin, "/api/ledger/trial-balance", ct);

    /// <summary>Polls the payment until its status satisfies <paramref name="done"/>; fails early when it reaches a final status that does not.</summary>
    public async Task<PaymentDto> WaitForPaymentAsync(string user, Guid paymentId, Func<string, bool> done, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        PaymentDto? last = null;
        var ended = false;
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                last = await GetPaymentAsync(user, paymentId, ct);
                if (!done(last.Status) && !InFlightPaymentStatuses.Contains(last.Status) && last.Status != "Authorized")
                {
                    ended = true;
                    throw new ShouldAssertException($"Payment {paymentId} ended {last.Status} ({last.FailureReason}).");
                }
                return done(last.Status);
            }, timeout ?? RetryWindow);
        }
        catch (ShouldAssertException error) when (!ended)
        {
            throw new ShouldAssertException($"Payment {paymentId} is still {last?.Status}. {error.Message}", error);
        }
        return last!;
    }

    public async Task<T> GetAsync<T>(string user, string path, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(user, () => new HttpRequestMessage(HttpMethod.Get, path), retryNotFound: false, ct);
        return await ReadAsync<T>(response, "GET " + path, ct);
    }

    public async Task<T> PostAsync<T>(string user, string path, object body, string idempotencyKey, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(user, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            return request;
        }, retryNotFound: true, ct);
        return await ReadAsync<T>(response, "POST " + path, ct);
    }

    public void Dispose() => http.Dispose();

    private async Task<HttpResponseMessage> SendWithRetryAsync(string user, Func<HttpRequestMessage> create, bool retryNotFound, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        try
        {
            await Eventually.ShouldAsync(async () =>
            {
                response?.Dispose();
                response = null;
                using var request = create();
                try
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(user, ct));
                    response = await http.SendAsync(request, ct);
                }
                catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // Gateway or Keycloak restarting, connection reset, Keycloak 5xx or HttpClient timeout: retry within the window.
                    return false;
                }
                if (Transient.Contains(response.StatusCode) || retryNotFound && response.StatusCode == HttpStatusCode.NotFound) return false;
                // The same key is still being processed (idempotency lease held): retry with the same key.
                return !(response.StatusCode == HttpStatusCode.Conflict &&
                    (await response.Content.ReadAsStringAsync(ct)).Contains("request-in-progress", StringComparison.Ordinal));
            }, RetryWindow);
        }
        catch (ShouldAssertException error)
        {
            var status = response is null ? "no response" : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            response?.Dispose();
            throw new ShouldAssertException($"The gateway kept answering {status} for {user}. {error.Message}", error);
        }
        return response!;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new ShouldAssertException($"{operation} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
