namespace Ledgerline.Reconciliation.Matching;

public enum MismatchCode
{
    BankCaptureNotInLedger,
    LedgerEntryNotAtBank,
    AmountMismatch,
    PaymentLedgerMismatch,
    RefundMissingAtBank,
    UnknownBankRecord,
    LedgerImbalance,
}
