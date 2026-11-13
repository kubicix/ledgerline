using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Auth;

public interface ICurrentUser
{
    Guid Id { get; }
    string? Email { get; }
    bool IsInRole(string role);
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid Id => Guid.Parse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("An authenticated user is required."));
    public string? Email => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email);
    public bool IsInRole(string role) => accessor.HttpContext?.User.IsInRole(role) == true;
}
