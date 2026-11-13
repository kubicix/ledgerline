using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Ledgerline.Accounts.Infrastructure.Entities;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Accounts.Infrastructure;

public static class SystemAccounts
{
    public const string BankClearing = "BANK_CLEARING";
    public const string PaymentsPending = "PAYMENTS_PENDING";
    private static readonly Guid NamespaceId = Guid.Parse("6f1c2a4e-0d53-4c8b-9a77-1e5b3c9d2f10");

    [SuppressMessage("Security", "CA5350", Justification = "UUIDv5 specifies SHA-1 for deterministic identifiers, not security.")]
    public static Guid IdFor(string code, string currency)
    {
        var bytes = NamespaceId.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(code + ":" + currency)).ToArray();
        var hash = SHA1.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    internal static async Task SeedAsync(AccountsDbContext db, IReadOnlyList<string> currencies, IClock clock, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var currency in currencies)
        foreach (var code in new[] { BankClearing, PaymentsPending })
            await SeedOneAsync(db, code, currency, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task SeedOneAsync(AccountsDbContext db, string code, string currency, DateTimeOffset now, CancellationToken ct)
    {
        var id = IdFor(code, currency);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts (id, type, system_code, currency, status, balance_minor, version, opened_at)
            VALUES ({id}, 'System', {code}, {currency}, 'Active', 0, 1, {now}) ON CONFLICT (id) DO NOTHING
            """, ct);
        if (inserted == 0) return;
        db.Events.Add(new LedgerEvent
        {
            StreamId = id, Version = 1, Type = "account-opened.v1", OccurredAt = now,
            Data = EnvelopeSerializer.ToPayload(new { OwnerId = (Guid?)null, Type = "System", SystemCode = code, Currency = currency, ContactEmail = (string?)null }),
            Metadata = EnvelopeSerializer.ToPayload(new { CorrelationId = id, CausationId = (Guid?)null, OperationId = (Guid?)null })
        });
    }
}
