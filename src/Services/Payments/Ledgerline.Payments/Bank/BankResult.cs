namespace Ledgerline.Payments.Bank;

public abstract record BankResult
{
    public sealed record Succeeded(string Id) : BankResult;
    public sealed record Declined(string Code) : BankResult;
    public sealed record Unknown : BankResult;
}
