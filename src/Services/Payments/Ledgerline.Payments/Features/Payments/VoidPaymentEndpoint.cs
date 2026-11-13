using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.Payments;

internal static class VoidPaymentEndpoint
{
    internal static Task<IResult> HandleAsync(Guid id, HttpContext http, PaymentsDbContext db,
        SagaRunner runner, ICurrentUser user, CancellationToken ct) =>
        PaymentActions.ExecuteAsync(id, false, null, http, db, runner, user, ct);
}
