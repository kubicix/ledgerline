using Ledgerline.BuildingBlocks;

namespace Ledgerline.BuildingBlocks.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_zero_and_negative_amounts(long amount) =>
        Should.Throw<ArgumentException>(() => Money.Create(amount, "TRY"));

    [Fact]
    public void Create_rejects_amount_above_maximum() =>
        Should.Throw<ArgumentException>(() => Money.Create(1_000_000_000_01, "TRY"));

    [Theory]
    [InlineData("try")]
    [InlineData("TRYX")]
    [InlineData("")]
    [InlineData("T1Y")]
    [InlineData(null)]
    public void Create_rejects_currency_not_three_upper_case_letters(string? currency) =>
        Should.Throw<ArgumentException>(() => Money.Create(1, currency!));

    [Fact]
    public void Add_with_different_currencies_throws_CurrencyMismatchException() =>
        Should.Throw<CurrencyMismatchException>(() => Money.Create(1, "TRY") + Money.Create(1, "USD"));

    [Fact]
    public void Money_rejects_overflow()
    {
        Should.Throw<OverflowException>(() => new Money(long.MaxValue, "TRY") + new Money(1, "TRY"));
        Should.Throw<OverflowException>(() => new Money(long.MinValue, "TRY") - new Money(1, "TRY"));
    }

    [Fact]
    public void Ids_New_returns_version_7_guid() => Ids.New().Version.ShouldBe(7);

    [Fact]
    public void Valid_money_preserves_amount_and_currency_and_supports_arithmetic()
    {
        Money.Create(1_000_000_000_00, "TRY").AmountMinor.ShouldBe(1_000_000_000_00);
        (Money.Create(200, "TRY") + Money.Create(50, "TRY")).ShouldBe(new Money(250, "TRY"));
        (Money.Create(200, "TRY") - Money.Create(50, "TRY")).ShouldBe(new Money(150, "TRY"));
        Should.Throw<CurrencyMismatchException>(() => new Money(1, "TRY") - new Money(1, "USD"));
    }
}
