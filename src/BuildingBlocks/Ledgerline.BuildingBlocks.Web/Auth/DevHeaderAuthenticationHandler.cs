using System.Security.Claims;
using System.Text.Encodings.Web;
using Ledgerline.BuildingBlocks.Web.Problems;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ledgerline.BuildingBlocks.Web.Auth;

public sealed class DevHeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevHeaders";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Dev-User", out var raw)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Guid.TryParse(raw.ToString(), out var id)) return Task.FromResult(AuthenticateResult.Fail("Invalid development user ID."));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()), new("sub", id.ToString()) };
        foreach (var role in Request.Headers["X-Dev-Roles"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (Request.Headers.TryGetValue("X-Dev-Email", out var email) && !string.IsNullOrWhiteSpace(email))
            claims.Add(new Claim(ClaimTypes.Email, email.ToString()));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProblemResults.For(401, ProblemCodes.Unauthenticated, "Authentication required").ExecuteAsync(Context);
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemResults.For(403, ProblemCodes.Forbidden, "Access forbidden").ExecuteAsync(Context);
}
