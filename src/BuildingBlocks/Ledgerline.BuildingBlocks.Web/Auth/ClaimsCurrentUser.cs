using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Auth;

public sealed class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid Id => Guid.Parse(accessor.HttpContext?.User.FindFirstValue("sub")
        ?? throw new InvalidOperationException("An authenticated user is required."));
    public string? Email => accessor.HttpContext?.User.FindFirstValue("email");
    public bool IsInRole(string role) => accessor.HttpContext?.User.IsInRole(role) == true;
}
