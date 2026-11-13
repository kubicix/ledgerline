using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Payments;

public sealed record TimelineResponse(string Step, string Outcome, DateTimeOffset At);

public sealed record PaymentResponse(Guid Id, string Kind, string Status, long AmountMinor, string Currency,
    Guid? SourceAccountId, Guid DestinationAccountId, string? CaptureMode, long CapturedMinor, long RefundedMinor,
    string? FailureReason, DateTimeOffset? AuthorizationExpiresAt, DateTimeOffset? ReconciledAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<TimelineResponse> Timeline)
{
    internal static async Task<PaymentResponse> FromAsync(Payment payment, PaymentsDbContext db, CancellationToken ct)
    {
        var timeline = await db.SagaSteps.AsNoTracking().Where(x => x.PaymentId == payment.Id).OrderBy(x => x.Id)
            .Select(x => new TimelineResponse(x.Step.ToString(), x.Outcome, x.At)).ToListAsync(ct);
        return new(payment.Id, payment.Kind.ToString(), payment.Status.ToString(), payment.AmountMinor, payment.Currency,
            payment.SourceAccountId, payment.DestinationAccountId, payment.CaptureMode?.ToString(), payment.CapturedMinor,
            payment.RefundedMinor, payment.FailureReason, payment.AuthorizationExpiresAt, payment.ReconciledAt,
            payment.CreatedAt, payment.UpdatedAt, timeline);
    }
}
