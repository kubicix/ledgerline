using System.Globalization;

namespace Ledgerline.Reconciliation.Runs;

/// <summary>The <c>Bank</c> section: where Bank Simulator listens, its API key, and how long one call may take.</summary>
public sealed class BankClientOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>The settlement file could not be fetched. The message holds no response content, at most a status code.</summary>
public sealed class SettlementUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Fetches the bank's settlement file (<c>GET settlements/{yyyy-MM-dd}</c>, reconciliation.md section 2). The second
/// outbound HTTP caller of the system besides Payments. The timeout is the client's own <see cref="HttpClient.Timeout"/>,
/// so an unreachable or silent bank fails the run in bounded time.
/// </summary>
public sealed class BankSettlementClient(HttpClient client)
{
    /// <exception cref="SettlementUnavailableException">A connection failure, a timeout or a status other than 2xx.</exception>
    public async Task<string> GetSettlementAsync(DateOnly businessDate, CancellationToken ct)
    {
        var path = new Uri("settlements/" + businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), UriKind.Relative);
        try
        {
            // The default completion option buffers the whole body, so the timeout covers reading it too.
            using var response = await client.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
                throw new SettlementUnavailableException($"The bank answered the settlement request with status {(int)response.StatusCode}.");
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException error)
        {
            throw new SettlementUnavailableException("The bank could not be reached for the settlement file.", error);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new SettlementUnavailableException("The bank did not answer the settlement request in time.", error);
        }
    }
}
