using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Features.Payments;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.Topups;

public sealed record CreateTopUpRequest(Guid AccountId, long AmountMinor, string Currency, string? CardToken);

internal static class CreateTopUpEndpoint
{
    internal static Task<IResult> HandleAsync(CreateTopUpRequest request, HttpContext http, PaymentsDbContext db,
        SagaRunner runner, ICurrentUser user, IClock clock, PaymentSagaOptions options, CancellationToken ct)
    {
        if (options.BankEnabled && string.IsNullOrWhiteSpace(request.CardToken))
            return Task.FromResult<IResult>(ProblemResults.Validation(new Dictionary<string, string[]> { ["cardToken"] = ["A card token is required."] }));
        return CreatePayment.HandleAsync(PaymentKind.TopUp, null, request.AccountId, request.AmountMinor, request.Currency,
            request.CardToken, http, db, runner, user, clock, ct);
    }
}
