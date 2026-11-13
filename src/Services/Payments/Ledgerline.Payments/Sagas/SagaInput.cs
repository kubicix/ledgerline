namespace Ledgerline.Payments.Sagas;

public abstract record SagaInput
{
    public sealed record RiskApproved : SagaInput;
    public sealed record RiskRejected(string Reason) : SagaInput;
    public sealed record LedgerSucceeded(Guid OperationId) : SagaInput;
    public sealed record LedgerRejected(Guid OperationId, string Reason) : SagaInput;
    public sealed record BankAuthorized(string Id) : SagaInput;
    public sealed record BankDeclined(string Code) : SagaInput;
    public sealed record BankCaptured(string Id) : SagaInput;
    public sealed record BankCaptureDeclined(string Code) : SagaInput;
    public sealed record BankCompensated : SagaInput;
    public sealed record CaptureRequested(long AmountMinor) : SagaInput;
    public sealed record VoidRequested : SagaInput;
    public sealed record AuthorizationExpired : SagaInput;
    public sealed record StepTimedOut(SagaStep Step) : SagaInput;
}
