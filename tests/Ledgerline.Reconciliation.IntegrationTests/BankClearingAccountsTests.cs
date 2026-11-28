using Ledgerline.Accounts.Infrastructure;
using Ledgerline.Reconciliation.Staging;

namespace Ledgerline.Reconciliation.IntegrationTests;

/// <summary>Reconciliation copies the deterministic id rule of accounts.md section 2; Accounts holds the original.</summary>
public sealed class BankClearingAccountsTests
{
    [Theory]
    [InlineData("TRY")]
    [InlineData("EUR")]
    [InlineData("USD")]
    public void Bank_clearing_ids_follow_the_accounts_rule(string currency) =>
        BankClearingAccounts.IdFor(currency).ShouldBe(SystemAccounts.IdFor(SystemAccounts.BankClearing, currency));

    [Fact]
    public void Bank_clearing_id_is_stable() =>
        BankClearingAccounts.IdFor("TRY").ShouldBe(Guid.Parse("762a998e-0eaa-5b60-b46e-e42d8ed9e3aa"));
}
