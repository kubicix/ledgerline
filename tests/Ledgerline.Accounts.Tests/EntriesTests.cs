using Ledgerline.Accounts.Ledger;
using Ledgerline.BuildingBlocks;

namespace Ledgerline.Accounts.Tests;

public sealed class EntriesTests
{
    private static readonly Guid Customer = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Merchant = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Bank = Guid.Parse("762a998e-0eaa-5b60-b46e-e42d8ed9e3aa");
    private static readonly Guid Pending = Guid.Parse("2c0a2564-5eeb-5be5-ba7f-375f277c90aa");
    private static readonly Guid Operation = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Payment = Guid.Parse("00000000-0000-0000-0000-000000000004");

    [Fact]
    public void TopUp_has_expected_lines() => AssertRequest(Entries.TopUp(Operation, Payment, Customer, Money.Create(100, "TRY")), EntryType.TopUp,
        [new(Bank, Direction.Debit, 100), new(Customer, Direction.Credit, 100)]);

    [Fact]
    public void Transfer_has_expected_lines() => AssertRequest(Entries.Transfer(Operation, Payment, Customer, Merchant, Money.Create(100, "TRY")), EntryType.Transfer,
        [new(Customer, Direction.Debit, 100), new(Merchant, Direction.Credit, 100)]);

    [Fact]
    public void Hold_has_expected_lines() => AssertRequest(Entries.Hold(Operation, Payment, Customer, Money.Create(100, "TRY")), EntryType.Hold,
        [new(Customer, Direction.Debit, 100), new(Pending, Direction.Credit, 100)]);

    [Fact]
    public void Capture_has_expected_lines() => AssertRequest(Entries.Capture(Operation, Payment, Customer, Merchant, 100, 70, "TRY"), EntryType.Capture,
        [new(Pending, Direction.Debit, 100), new(Merchant, Direction.Credit, 70), new(Customer, Direction.Credit, 30)]);

    [Fact]
    public void Release_has_expected_lines() => AssertRequest(Entries.Release(Operation, Payment, Customer, Money.Create(100, "TRY")), EntryType.Release,
        [new(Pending, Direction.Debit, 100), new(Customer, Direction.Credit, 100)]);

    [Fact]
    public void Refund_has_expected_lines() => AssertRequest(Entries.Refund(Operation, Payment, Merchant, Customer, Money.Create(100, "TRY")), EntryType.Refund,
        [new(Merchant, Direction.Debit, 100), new(Customer, Direction.Credit, 100)]);

    [Fact]
    public void Capture_with_full_amount_has_two_lines()
    {
        var request = Entries.Capture(Operation, Payment, Customer, Merchant, 100, 100, "TRY");
        request.Lines.ShouldBe([new PostingLine(Pending, Direction.Debit, 100), new PostingLine(Merchant, Direction.Credit, 100)]);
    }

    [Fact]
    public void Capture_with_partial_amount_credits_remainder_to_customer()
    {
        var request = Entries.Capture(Operation, Payment, Customer, Merchant, 100, 1, "TRY");
        request.Lines.Single(x => x.AccountId == Customer).ShouldBe(new PostingLine(Customer, Direction.Credit, 99));
        PostingValidator.EnsureWellFormed(request);
    }

    [Fact]
    public void Capture_and_Release_skip_status_check_for_customer()
    {
        Entries.Capture(Operation, Payment, Customer, Merchant, 100, 70, "TRY").SkipStatusCheckFor.ShouldBe([Customer]);
        Entries.Release(Operation, Payment, Customer, Money.Create(100, "TRY")).SkipStatusCheckFor.ShouldBe([Customer]);
        Entries.Hold(Operation, Payment, Customer, Money.Create(100, "TRY")).SkipStatusCheckFor.ShouldBeEmpty();
    }

    private static void AssertRequest(PostingRequest request, EntryType type, PostingLine[] lines)
    {
        request.OperationId.ShouldBe(Operation);
        request.PaymentId.ShouldBe(Payment);
        request.Currency.ShouldBe("TRY");
        request.EntryType.ShouldBe(type);
        request.Lines.ShouldBe(lines);
        PostingValidator.EnsureWellFormed(request);
    }
}
