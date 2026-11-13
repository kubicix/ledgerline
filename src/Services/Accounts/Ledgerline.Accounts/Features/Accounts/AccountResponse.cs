using Ledgerline.Accounts.Infrastructure.Entities;

namespace Ledgerline.Accounts.Features.Accounts;

public sealed record AccountResponse(Guid Id, Guid? OwnerId, string Type, string Currency, string Status,
    long BalanceMinor, long HeldMinor, int Version, DateTimeOffset OpenedAt)
{
    internal static AccountResponse From(Account account, long heldMinor = 0) =>
        new(account.Id, account.OwnerId, account.Type, account.Currency, account.Status,
            account.BalanceMinor, heldMinor, account.Version, account.OpenedAt);
}
