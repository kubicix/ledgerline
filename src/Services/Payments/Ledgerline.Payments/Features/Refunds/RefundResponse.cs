using Ledgerline.BuildingBlocks.Web.Idempotency;
using Ledgerline.Payments.Infrastructure.Entities;

namespace Ledgerline.Payments.Features.Refunds;

public sealed record RefundResponse(Guid Id, Guid PaymentId, long AmountMinor, string Status,
    string? FailureReason, DateTimeOffset CreatedAt) : IIdempotentResource
{
    internal static RefundResponse From(Refund refund) => new(refund.Id, refund.PaymentId, refund.AmountMinor,
        refund.Status.ToString(), refund.FailureReason, refund.CreatedAt);
}
