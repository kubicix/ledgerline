using Ledgerline.Accounts.Ledger;

namespace Ledgerline.Accounts.Tests;

public sealed class PostingValidatorTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void Rejects_single_line() => Should.Throw<MalformedPostingException>(() =>
        PostingValidator.EnsureWellFormed(Request(new PostingLine(First, Direction.Debit, 10))));

    [Fact]
    public void Rejects_unbalanced_lines() => Should.Throw<MalformedPostingException>(() =>
        PostingValidator.EnsureWellFormed(Request(new(First, Direction.Debit, 10), new(Second, Direction.Credit, 9))));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_amount(long amount) => Should.Throw<MalformedPostingException>(() =>
        PostingValidator.EnsureWellFormed(Request(new(First, Direction.Debit, amount), new(Second, Direction.Credit, amount))));

    [Fact]
    public void Rejects_same_account_twice() => Should.Throw<MalformedPostingException>(() =>
        PostingValidator.EnsureWellFormed(Request(new(First, Direction.Debit, 10), new(First, Direction.Credit, 10))));

    [Fact]
    public void Accepts_balanced_unique_positive_lines() => Should.NotThrow(() =>
        PostingValidator.EnsureWellFormed(Request(new(First, Direction.Debit, 10), new(Second, Direction.Credit, 10))));

    [Fact]
    public void Rejects_overflowing_totals() => Should.Throw<OverflowException>(() =>
        PostingValidator.EnsureWellFormed(Request(new(First, Direction.Debit, long.MaxValue),
            new(Second, Direction.Debit, 1), new(Guid.NewGuid(), Direction.Credit, long.MaxValue))));

    private static PostingRequest Request(params PostingLine[] lines) => new(Guid.NewGuid(), Guid.NewGuid(), EntryType.Transfer, "TRY", lines, new HashSet<Guid>());
}
