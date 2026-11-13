namespace Ledgerline.Payments.Sagas;

public sealed class PaymentSagaOptions
{
    public bool RiskEnabled { get; init; }
    public bool BankEnabled { get; init; }
    public int BankCompensationMaxAttempts { get; init; } = 8;
}
