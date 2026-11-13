using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.Payments;

public sealed record CapturePaymentRequest(long AmountMinor);

internal static class CapturePaymentEndpoint
{
    internal static Task<IResult> HandleAsync(Guid id, CapturePaymentRequest? request, HttpContext http,
        PaymentsDbContext db, SagaRunner runner, ICurrentUser user, CancellationToken ct) =>
        PaymentActions.ExecuteAsync(id, true, request?.AmountMinor, http, db, runner, user, ct);
}
