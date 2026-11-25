using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Ledgerline.SystemTests.Harness;

/// <summary>
/// Real Keycloak access tokens for the seeded realm users (password grant through the public
/// <c>ledgerline-k6</c> client, like scripts/smoke.sh). Tokens are cached per user and renewed before they expire.
/// </summary>
public sealed class KeycloakTokens(HttpClient http, Uri realmUrl)
{
    public const string Alice = "alice";
    public const string Bob = "bob";
    public const string Shop = "shop";
    public const string Admin = "admin";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, CachedToken> cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    public static Uri DefaultRealmUrl => new(Environment.GetEnvironmentVariable("KEYCLOAK_URL") ?? "http://localhost:8081/realms/ledgerline");

    /// <summary>
    /// Returns a cached token or fetches one; concurrent callers for the same user share one password grant.
    /// A 5xx answer from Keycloak throws <see cref="HttpRequestException"/> (transient); a 4xx answer throws
    /// <see cref="InvalidOperationException"/> (wrong user or password).
    /// </summary>
    public async Task<string> GetAsync(string username, CancellationToken ct = default)
    {
        if (TryCached(username, out var token)) return token;
        var refresh = locks.GetOrAdd(username, _ => new SemaphoreSlim(1, 1));
        await refresh.WaitAsync(ct);
        try
        {
            if (TryCached(username, out token)) return token;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "ledgerline-k6", ["username"] = username, ["password"] = PasswordOf(username)
            });
            using var response = await http.PostAsync(new Uri(realmUrl + "/protocol/openid-connect/token"), form, ct);
            if ((int)response.StatusCode >= 500)
                throw new HttpRequestException($"Keycloak token request for {username} failed with {(int)response.StatusCode}.", null, response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Keycloak token request for {username} failed with {(int)response.StatusCode}.");
            var fresh = (await response.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
            cache[username] = new CachedToken(fresh.AccessToken, DateTimeOffset.UtcNow.AddSeconds(fresh.ExpiresIn));
            return fresh.AccessToken;
        }
        finally
        {
            refresh.Release();
        }
    }

    private bool TryCached(string username, out string token)
    {
        var found = cache.TryGetValue(username, out var cached) && cached.ExpiresAt - RenewBefore > DateTimeOffset.UtcNow;
        token = found ? cached!.AccessToken : "";
        return found;
    }

    // Development passwords from deploy/compose/keycloak/ledgerline-realm.json; overridable like scripts/smoke.sh.
    private static string PasswordOf(string username) =>
        Environment.GetEnvironmentVariable(username.ToUpperInvariant() + "_PASSWORD") ?? username + "_dev";

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
