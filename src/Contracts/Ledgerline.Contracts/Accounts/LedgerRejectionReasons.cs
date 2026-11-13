namespace Ledgerline.Contracts.Accounts;

public static class LedgerRejectionReasons
{
    public const string AccountNotFound = "AccountNotFound";
    public const string AccountFrozen = "AccountFrozen";
    public const string AccountClosed = "AccountClosed";
    public const string CurrencyMismatch = "CurrencyMismatch";
    public const string InsufficientFunds = "InsufficientFunds";
    public const string SameAccount = "SameAccount";
    public const string HoldNotFound = "HoldNotFound";
    public const string HoldNotActive = "HoldNotActive";
    public const string AmountExceedsHold = "AmountExceedsHold";
    public const string InvalidAmount = "InvalidAmount";
}
