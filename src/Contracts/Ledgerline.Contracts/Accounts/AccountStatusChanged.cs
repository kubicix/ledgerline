using Ledgerline.BuildingBlocks.Messaging;

namespace Ledgerline.Contracts.Accounts;

[MessageType("accounts.event.account-status-changed.v1")]
public sealed record AccountStatusChanged(Guid AccountId, string Status, DateTimeOffset ChangedAt, int Version);
