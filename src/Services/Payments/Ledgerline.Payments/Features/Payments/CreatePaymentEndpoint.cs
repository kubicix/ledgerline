using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Sagas;

namespace Ledgerline.Payments.Features.Payments;

public sealed record CreateMerchantPaymentRequest(Guid CustomerAccountId, Guid MerchantAccountId, long AmountMinor, string Currency, string CaptureMode);

internal static class CreatePaymentEndpoint
{
    internal static Task<IResult> HandleAsync(CreateMerchantPaymentRequest request, HttpContext http, PaymentsDbContext db,
        SagaRunner runner, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        if (request.CaptureMode is not ("Manual" or "Automatic"))
            return Task.FromResult<IResult>(ProblemResults.Validation(new Dictionary<string, string[]> { ["captureMode"] = ["Use Manual or Automatic."] }));
        return CreatePayment.HandleAsync(PaymentKind.MerchantPayment, request.CustomerAccountId, request.MerchantAccountId,
            request.AmountMinor, request.Currency, null, http, db, runner, user, clock, ct, Enum.Parse<CaptureMode>(request.CaptureMode));
    }
}
