using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Ledgerline.Reconciliation.Staging;

/// <summary>
/// The <c>BANK_CLEARING</c> account IDs, computed with the deterministic rule of accounts.md section 2 (UUIDv5 of
/// <c>"&lt;code&gt;:&lt;currency&gt;"</c>), copied from Accounts' <c>SystemAccounts.IdFor</c> so Reconciliation needs no
/// call to Accounts. A test pins the copy against the original.
/// </summary>
public static class BankClearingAccounts
{
    private const string Code = "BANK_CLEARING";
    private static readonly Guid NamespaceId = Guid.Parse("6f1c2a4e-0d53-4c8b-9a77-1e5b3c9d2f10");

    [SuppressMessage("Security", "CA5350", Justification = "UUIDv5 specifies SHA-1 for deterministic identifiers, not security.")]
    public static Guid IdFor(string currency)
    {
        var bytes = NamespaceId.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(Code + ":" + currency)).ToArray();
        var hash = SHA1.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
