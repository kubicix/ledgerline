using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Ledgerline.Payments.Sagas;

public static class OperationIds
{
    public static Guid For(Guid paymentId, SagaStep step) => ForName(paymentId, step.ToString());

    public static Guid ForCapture(Guid paymentId, string requestKey) => ForName(paymentId, "LedgerCapture:" + requestKey);
    public static Guid ForRefund(Guid refundId) => ForName(refundId, "RefundFunds");

    [SuppressMessage("Security", "CA5350", Justification = "UUIDv5 specifies SHA-1 for deterministic identifiers, not security.")]
    private static Guid ForName(Guid paymentId, string name)
    {
        var bytes = paymentId.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(name)).ToArray();
        var hash = SHA1.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
