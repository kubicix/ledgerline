using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.Payments.Features.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.Transfers;

public sealed record CreateTransferRequest(Guid FromAccountId, Guid ToAccountId, long AmountMinor, string Currency);

internal static class CreateTransferEndpoint
{
    internal static Task<IResult> HandleAsync(CreateTransferRequest request, HttpContext http, PaymentsDbContext db,
        SagaRunner runner, ICurrentUser user, IClock clock, CancellationToken ct) =>
        CreatePayment.HandleAsync(PaymentKind.Transfer, request.FromAccountId, request.ToAccountId, request.AmountMinor,
            request.Currency, null, http, db, runner, user, clock, ct);
}
