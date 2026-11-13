using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Ledgerline.Payments.Sagas;

public static class OperationIds
{
    [SuppressMessage("Security", "CA5350", Justification = "UUIDv5 specifies SHA-1 for deterministic identifiers, not security.")]
    public static Guid For(Guid paymentId, SagaStep step)
    {
        var bytes = paymentId.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(step.ToString())).ToArray();
        var hash = SHA1.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
