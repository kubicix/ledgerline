namespace Ledgerline.BuildingBlocks;

public readonly record struct Money(long AmountMinor, string Currency)
{
    public const long MaxAmountMinor = 1_000_000_000_00;

    public static Money Create(long amountMinor, string currency)
    {
        if (amountMinor <= 0 || amountMinor > MaxAmountMinor)
            throw new ArgumentException("Amount must be positive and at most the maximum.", nameof(amountMinor));
        if (currency is null || currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must contain three upper-case letters.", nameof(currency));
        return new Money(amountMinor, currency);
    }

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.AmountMinor + right.AmountMinor), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.AmountMinor - right.AmountMinor), left.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
            throw new CurrencyMismatchException();
    }
}
