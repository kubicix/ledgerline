namespace Ledgerline.BuildingBlocks;

public sealed class CurrencyMismatchException : Exception
{
    public CurrencyMismatchException() : base("Currencies must match.") { }
    public CurrencyMismatchException(string message) : base(message) { }
    public CurrencyMismatchException(string message, Exception innerException) : base(message, innerException) { }
}
