using Ledgerline.BuildingBlocks;

namespace Ledgerline.Payments.Bank;

public interface IBankClient
{
    Task<BankResult> AuthorizeAsync(Guid paymentId, Money amount, string cardToken, CancellationToken ct);
    Task<BankResult> CaptureAsync(Guid paymentId, string authorizationId, CancellationToken ct);
    Task<BankResult> VoidAsync(Guid paymentId, string authorizationId, CancellationToken ct);
    Task<BankResult> RefundAsync(Guid paymentId, string captureId, CancellationToken ct);
    Task<BankResult> LookupAsync(string idempotencyKey, CancellationToken ct);
}
