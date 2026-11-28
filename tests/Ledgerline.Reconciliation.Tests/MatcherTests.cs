using Ledgerline.Reconciliation.Matching;

namespace Ledgerline.Reconciliation.Tests;

public sealed class MatcherTests
{
    private static readonly DateOnly Day = new(2026, 11, 13);
    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid P1 = Guid.Parse("0193a7f2-5a00-7000-8000-000000000001");
    private static readonly Guid P2 = Guid.Parse("0193a7f2-5a00-7000-8000-000000000002");
    private static readonly Guid P3 = Guid.Parse("0193a7f2-5a00-7000-8000-000000000003");
    private static readonly Guid Clearing = Guid.Parse("00000000-0000-5000-8000-0000000000c1");
    private static readonly Guid Wallet = Guid.Parse("00000000-0000-5000-8000-0000000000a1");

    private static BankRecord Capture(Guid payment, long amount = 1000, string currency = "TRY", DateTimeOffset? at = null) =>
        new(BankRecordType.Capture, "cap_" + payment.ToString("N")[^4..], payment.ToString(), amount, currency, at ?? Noon);

    private static BankRecord Refund(Guid payment, long amount = 1000, string currency = "TRY") =>
        new(BankRecordType.Refund, "ref_" + payment.ToString("N")[^4..], payment.ToString(), amount, currency, Noon);

    private static StagedPayment Payment(Guid id, string status = "Completed", long amount = 1000, string currency = "TRY",
        string kind = "TopUp", DateTimeOffset? at = null) =>
        new(id, kind, status, amount, currency, null, at ?? Noon);

    /// <summary>A balanced TopUp entry: debit BANK_CLEARING, credit the wallet.</summary>
    private static StagedLedgerLine[] TopUp(Guid payment, long amount = 1000, string currency = "TRY", DateTimeOffset? at = null)
    {
        var entry = Guid.NewGuid();
        var posted = at ?? Noon;
        return
        [
            new(entry, 1, Clearing, true, "TopUp", "Debit", amount, currency, payment, posted),
            new(entry, 2, Wallet, false, "TopUp", "Credit", amount, currency, payment, posted),
        ];
    }

    private static ReconciliationResult Run(BankRecord[] bank, StagedPayment[] payments, StagedLedgerLine[] ledger) =>
        Matcher.Match(bank, payments, ledger, Day);

    [Fact]
    public void Clean_day_has_no_mismatches()
    {
        var result = Run(
            [Capture(P1), Capture(P2), Refund(P2)],
            [Payment(P1), Payment(P2, "Compensated"), Payment(P3, "Compensated")],
            TopUp(P1));

        result.Mismatches.ShouldBeEmpty();
        result.MatchedPaymentIds.ShouldBe([P1, P2, P3]);
    }

    [Fact]
    public void Detects_BankCaptureNotInLedger()
    {
        var result = Run([Capture(P1, 1500)], [Payment(P1, "ManualReview", 1500)], []);

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.BankCaptureNotInLedger, P1.ToString(), 1500, null, "TRY")]);
        result.MatchedPaymentIds.ShouldBeEmpty();
    }

    [Fact]
    public void Detects_LedgerEntryNotAtBank()
    {
        var result = Run([], [Payment(P1)], TopUp(P1, 2000));

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.LedgerEntryNotAtBank, P1.ToString(), null, 2000, "TRY")]);
    }

    [Fact]
    public void Detects_AmountMismatch()
    {
        var result = Run([Capture(P1, 1000)], [Payment(P1)], TopUp(P1, 900));

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.AmountMismatch, P1.ToString(), 1000, 900, "TRY")]);
    }

    [Fact]
    public void Detects_AmountMismatch_when_only_the_currency_differs()
    {
        var result = Run([Capture(P1, 1000, "TRY")], [Payment(P1)], TopUp(P1, 1000, "USD"));

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.AmountMismatch, P1.ToString(), 1000, 1000, "TRY")]);
    }

    [Fact]
    public void Detects_PaymentLedgerMismatch()
    {
        var result = Run([], [Payment(P1)], []);

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.PaymentLedgerMismatch, P1.ToString(), null, null, "TRY")]);
    }

    [Fact]
    public void Detects_PaymentLedgerMismatch_for_a_ledger_entry_of_a_payment_that_is_not_completed()
    {
        var result = Run([Capture(P1)], [Payment(P1, "ManualReview")], TopUp(P1));

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.PaymentLedgerMismatch, P1.ToString(), null, 1000, "TRY")]);
    }

    [Fact]
    public void Detects_RefundMissingAtBank()
    {
        var result = Run([Capture(P1, 700)], [Payment(P1, "Compensated", 700)], []);

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.RefundMissingAtBank, P1.ToString(), 700, null, "TRY")]);
    }

    [Fact]
    public void Detects_UnknownBankRecord()
    {
        var unknown = Guid.Parse("0193a7f2-5a00-7000-8000-0000000000ff");

        var result = Run([Capture(unknown, 400)], [], []);

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.UnknownBankRecord, unknown.ToString(), 400, null, "TRY")]);
    }

    [Fact]
    public void Unknown_bank_reference_that_is_not_a_guid_is_reported_with_the_raw_reference()
    {
        var row = new BankRecord(BankRecordType.Capture, "cap_x", "not-a-guid", 5, "TRY", Noon);

        Run([row], [], []).Mismatches.ShouldBe([new Mismatch(MismatchCode.UnknownBankRecord, "not-a-guid", 5, null, "TRY")]);
    }

    [Fact]
    public void Unknown_bank_record_supersedes_the_other_bank_derived_codes()
    {
        var unknown = Guid.NewGuid();

        var result = Run([Capture(unknown), Refund(unknown)], [], []);

        result.Mismatches.Select(m => m.Code).ShouldBe([MismatchCode.UnknownBankRecord, MismatchCode.UnknownBankRecord]);
    }

    [Fact]
    public void A_capture_just_before_midnight_of_a_payment_that_finished_later_is_not_unknown()
    {
        var late = new DateTimeOffset(2026, 11, 14, 0, 0, 3, TimeSpan.Zero);

        var result = Run([Capture(P1, at: new DateTimeOffset(2026, 11, 13, 23, 59, 59, TimeSpan.Zero))],
            [Payment(P1, "ManualReview", at: late)], []);

        result.Mismatches.Select(m => m.Code).ShouldBe([MismatchCode.BankCaptureNotInLedger]);
    }

    [Fact]
    public void Completed_payment_with_a_bank_capture_and_no_ledger_entry_gives_both_codes()
    {
        var result = Run([Capture(P1)], [Payment(P1)], []);

        result.Mismatches.Select(m => m.Code).ShouldBe([MismatchCode.BankCaptureNotInLedger, MismatchCode.PaymentLedgerMismatch]);
    }

    [Fact]
    public void Compensated_topup_with_capture_and_refund_is_matched()
    {
        var result = Run([Capture(P1), Refund(P1)], [Payment(P1, "Compensated")], []);

        result.Mismatches.ShouldBeEmpty();
        result.MatchedPaymentIds.ShouldBe([P1]);
    }

    [Fact]
    public void Records_outside_business_date_are_ignored()
    {
        var yesterday = new DateTimeOffset(2026, 11, 12, 23, 59, 59, TimeSpan.Zero);
        var tomorrow = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

        var result = Run(
            [Capture(P1, at: yesterday), Capture(P2, at: tomorrow)],
            [Payment(P1, at: yesterday), Payment(P2, at: tomorrow), Payment(P3, "Compensated", at: tomorrow)],
            [.. TopUp(P1, at: yesterday), .. TopUp(P2, at: tomorrow)]);

        result.Mismatches.ShouldBeEmpty();
        result.MatchedPaymentIds.ShouldBeEmpty();
    }

    [Fact]
    public void The_window_includes_midnight_at_its_start_and_excludes_it_at_its_end()
    {
        var start = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

        var result = Run([Capture(P1, at: start), Capture(P2, at: end)],
            [Payment(P1, "ManualReview", at: start), Payment(P2, "ManualReview", at: end)], []);

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.BankCaptureNotInLedger, P1.ToString(), 1000, null, "TRY")]);
    }

    [Fact]
    public void Imbalanced_ledger_lines_give_LedgerImbalance()
    {
        var entry = Guid.NewGuid();
        StagedLedgerLine[] lines =
        [
            new(entry, 1, Clearing, true, "TopUp", "Debit", 1000, "TRY", P1, Noon),
            new(entry, 2, Wallet, false, "TopUp", "Credit", 900, "TRY", P1, Noon),
            .. TopUp(P2, 500, "USD"),
        ];

        var result = Run([Capture(P1), Capture(P2, 500, "USD")], [Payment(P1), Payment(P2, amount: 500, currency: "USD")], lines);

        result.Mismatches.ShouldBe([new Mismatch(MismatchCode.LedgerImbalance, "TRY", null, -100, "TRY")]);
    }

    [Fact]
    public void The_trial_balance_counts_earlier_days_but_not_later_ones()
    {
        var earlier = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        var lone = new StagedLedgerLine(Guid.NewGuid(), 1, Wallet, false, "Transfer", "Credit", 70, "TRY", Guid.NewGuid(), earlier);
        var afterTheDay = new StagedLedgerLine(Guid.NewGuid(), 1, Wallet, false, "Transfer", "Credit", 30, "TRY", Guid.NewGuid(), later);

        Run([], [], [lone]).Mismatches.ShouldBe([new Mismatch(MismatchCode.LedgerImbalance, "TRY", null, 70, "TRY")]);
        Run([], [], [afterTheDay]).Mismatches.ShouldBeEmpty();
    }

    [Fact]
    public void LedgerImbalance_does_not_unmatch_a_payment()
    {
        var lone = new StagedLedgerLine(Guid.NewGuid(), 1, Wallet, false, "Transfer", "Credit", 70, "TRY", Guid.NewGuid(), Noon);

        var result = Run([Capture(P1), Refund(P1)], [Payment(P1, "Compensated")], [lone]);

        result.MatchedPaymentIds.ShouldBe([P1]);
        result.Mismatches.Select(m => m.Code).ShouldBe([MismatchCode.LedgerImbalance]);
    }

    [Fact]
    public void Voided_before_capture_and_other_non_topup_payments_are_not_in_the_matched_set()
    {
        var result = Run([], [Payment(P1, "Voided"), Payment(P2, kind: "Transfer"), Payment(P3, "Failed")], []);

        result.Mismatches.ShouldBeEmpty();
        result.MatchedPaymentIds.ShouldBeEmpty();
    }

    [Fact]
    public void The_result_does_not_depend_on_input_order()
    {
        BankRecord[] bank = [Capture(P1), Capture(P2, 5), Capture(Guid.Parse("0193a7f2-5a00-7000-8000-0000000000ff")), Refund(P3)];
        StagedPayment[] payments = [Payment(P1), Payment(P2, "Compensated", 5), Payment(P3, "Compensated")];
        StagedLedgerLine[] ledger = [.. TopUp(P1, 999), .. TopUp(P3)];

        var forward = Run(bank, payments, ledger);
        var reversed = Run([.. bank.Reverse()], [.. payments.Reverse()], [.. ledger.Reverse()]);

        reversed.Mismatches.ShouldBe(forward.Mismatches);
        reversed.MatchedPaymentIds.ShouldBe(forward.MatchedPaymentIds);
        forward.Mismatches.Count.ShouldBeGreaterThan(3);
    }

    [Fact]
    public void Repeated_rows_for_one_reference_are_summed()
    {
        var result = Run([Capture(P1, 600), Capture(P1, 400)], [Payment(P1)], TopUp(P1, 1000));

        result.Mismatches.ShouldBeEmpty();
    }
}
