namespace Ledgerline.Payments.Sagas;

public enum SagaStep
{
    None, RiskCheck, BankAuthorize, BankCapture, LedgerCredit, LedgerTransfer,
    LedgerReserve, LedgerCapture, LedgerRelease, BankVoid, BankRefund
}
