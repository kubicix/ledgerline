using System.Net;
using System.Text.Json;
using Ledgerline.BuildingBlocks;
using Polly.Timeout;

namespace Ledgerline.Payments.Bank;

public sealed class BankClient(HttpClient client) : IBankClient
{
    public Task<BankResult> AuthorizeAsync(Guid paymentId, Money amount, string cardToken, CancellationToken ct) =>
        PostAsync(paymentId, "authorize", "/authorizations", new { amount.AmountMinor, amount.Currency, cardToken, reference = paymentId.ToString() }, ct);
    public Task<BankResult> CaptureAsync(Guid paymentId, string authorizationId, CancellationToken ct) =>
        PostAsync(paymentId, "capture", $"/authorizations/{Uri.EscapeDataString(authorizationId)}/capture", null, ct);
    public Task<BankResult> VoidAsync(Guid paymentId, string authorizationId, CancellationToken ct) =>
        PostAsync(paymentId, "void", $"/authorizations/{Uri.EscapeDataString(authorizationId)}/void", null, ct);
    public Task<BankResult> RefundAsync(Guid paymentId, string captureId, CancellationToken ct) =>
        PostAsync(paymentId, "refund", $"/captures/{Uri.EscapeDataString(captureId)}/refund", null, ct);

    public async Task<BankResult> LookupAsync(string idempotencyKey, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync("/operations/" + Uri.EscapeDataString(idempotencyKey), ct);
            if (!response.IsSuccessStatusCode) return new BankResult.Unknown();
            var stored = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return Parse((HttpStatusCode)stored.GetProperty("httpStatus").GetInt32(), stored.GetProperty("body"));
        }
        catch (HttpRequestException) { return new BankResult.Unknown(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new BankResult.Unknown(); }
        catch (TimeoutRejectedException) { return new BankResult.Unknown(); }
        catch (JsonException) { return new BankResult.Unknown(); }
    }

    private async Task<BankResult> PostAsync(Guid paymentId, string operation, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Idempotency-Key", Key(paymentId, operation));
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode >= 500) return new BankResult.Unknown();
            return Parse(response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(ct));
        }
        catch (HttpRequestException) { return new BankResult.Unknown(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new BankResult.Unknown(); }
        catch (TimeoutRejectedException) { return new BankResult.Unknown(); }
        catch (JsonException) { return new BankResult.Unknown(); }
    }

    private static BankResult Parse(HttpStatusCode status, JsonElement body)
    {
        if ((int)status >= 500) return new BankResult.Unknown();
        if ((int)status >= 400) return new BankResult.Declined(body.TryGetProperty("declineCode", out var code)
            ? code.GetString()! : "Http" + (int)status);
        if (body.TryGetProperty("authorizationId", out var id) || body.TryGetProperty("captureId", out id) || body.TryGetProperty("refundId", out id))
            return new BankResult.Succeeded(id.GetString()!);
        return body.TryGetProperty("status", out var state) && state.GetString() == "Voided"
            ? new BankResult.Succeeded("") : new BankResult.Unknown();
    }

    internal static string Key(Guid paymentId, string operation) => $"{paymentId}:{operation.ToLowerInvariant()}";
}
