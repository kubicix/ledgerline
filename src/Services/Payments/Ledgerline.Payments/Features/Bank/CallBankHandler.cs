using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Payments;
using Ledgerline.Payments.Bank;
using Ledgerline.Payments.Infrastructure;
using Ledgerline.Payments.Infrastructure.Entities;
using Ledgerline.Payments.Sagas;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Payments.Features.Bank;

public sealed class CallBankHandler(PaymentsDbContext db, IBankClient bank, SagaRunner runner, ICrashPoint crash) : IMessageHandler<CallBank>
{
    public async Task HandleAsync(CallBank message, MessageContext context, CancellationToken ct)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.Id == message.PaymentId, ct);
        if (payment is null || !Matches(payment.SagaStep, message.Operation)) return;
        var result = await CallAsync(payment, message.Operation, ct);
        if (result is BankResult.Unknown) result = await bank.LookupAsync(BankClient.Key(payment.Id, message.Operation), ct);
        if (result is BankResult.Unknown) throw new InvalidOperationException("The bank operation has no definitive result; retry the same key.");
        crash.Hit("bank.after-response");
        await runner.ApplyAsync(payment.Id, Input(message.Operation, result), context, ct);
    }

    private Task<BankResult> CallAsync(Payment payment, string operation, CancellationToken ct) => operation switch
    {
        "Authorize" => bank.AuthorizeAsync(payment.Id, Money.Create(payment.AmountMinor, payment.Currency), payment.CardToken!, ct),
        "Capture" => bank.CaptureAsync(payment.Id, payment.BankAuthorizationId!, ct),
        "Void" => bank.VoidAsync(payment.Id, payment.BankAuthorizationId!, ct),
        "Refund" => bank.RefundAsync(payment.Id, payment.BankCaptureId!, ct),
        _ => throw new NotSupportedException("Unknown bank operation.")
    };

    private static bool Matches(SagaStep step, string operation) => (step, operation) is
        (SagaStep.BankAuthorize, "Authorize") or (SagaStep.BankCapture, "Capture") or
        (SagaStep.BankVoid, "Void") or (SagaStep.BankRefund, "Refund");

    private static SagaInput Input(string operation, BankResult result) => (operation, result) switch
    {
        ("Authorize", BankResult.Succeeded success) => new SagaInput.BankAuthorized(success.Id),
        ("Authorize", BankResult.Declined declined) => new SagaInput.BankDeclined(declined.Code),
        ("Capture", BankResult.Succeeded success) => new SagaInput.BankCaptured(success.Id),
        ("Capture", BankResult.Declined declined) => new SagaInput.BankCaptureDeclined(declined.Code),
        ("Void" or "Refund", BankResult.Succeeded) => new SagaInput.BankCompensated(),
        _ => throw new InvalidOperationException("Bank compensation did not succeed; retry the same key.")
    };
}
