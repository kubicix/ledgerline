using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Ledgerline.Testing;

public static class TestTokens
{
    public const string Issuer = "https://ledgerline.test";
    public static string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Create(Guid userId, string roles, string? email) =>
        Create(userId, roles, email, "ledgerline-api", DateTimeOffset.UtcNow.AddHours(1));

    public static string Create(Guid userId, string roles, string? email, string audience, DateTimeOffset expiresAt)
    {
        List<Claim> claims = [new("sub", userId.ToString())];
        claims.AddRange(roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(role => new Claim("roles", role)));
        if (email is not null) claims.Add(new("email", email));
        var signing = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, audience, claims, expiresAt.AddHours(-1).UtcDateTime, expiresAt.UtcDateTime, signing);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
