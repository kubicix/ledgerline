namespace Ledgerline.BuildingBlocks.Web.Auth;

public interface ICurrentUser
{
    Guid Id { get; }
    string? Email { get; }
    bool IsInRole(string role);
}
