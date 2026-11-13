namespace Ledgerline.Accounts.Ledger;

public static class PostingValidator
{
    public static void EnsureWellFormed(PostingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count < 2) throw new MalformedPostingException("A posting needs at least two lines.");
        var accounts = new HashSet<Guid>();
        long debits = 0, credits = 0;
        foreach (var line in request.Lines)
        {
            if (line.AmountMinor <= 0) throw new MalformedPostingException("Line amounts must be positive.");
            if (!accounts.Add(line.AccountId)) throw new MalformedPostingException("An account cannot appear twice.");
            if (line.Direction == Direction.Debit) debits = checked(debits + line.AmountMinor);
            else credits = checked(credits + line.AmountMinor);
        }
        if (debits != credits) throw new MalformedPostingException("Debits and credits must balance.");
    }
}

public sealed class MalformedPostingException : Exception
{
    public MalformedPostingException() { }
    public MalformedPostingException(string message) : base(message) { }
    public MalformedPostingException(string message, Exception innerException) : base(message, innerException) { }
}
