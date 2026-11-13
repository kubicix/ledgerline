using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.event.account-opened.v1")]
public sealed record AccountOpened(Guid AccountId, Guid? OwnerId, string Type, string Currency, string? ContactEmail, DateTimeOffset OpenedAt);
