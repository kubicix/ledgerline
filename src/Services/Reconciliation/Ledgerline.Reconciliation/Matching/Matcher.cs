namespace Ledgerline.Reconciliation.Matching;

/// <summary>
/// The three-way match of reconciliation.md section 3 steps 5 and 6. Pure: no I/O, no clock, no logging. The result does
/// not depend on the order of the inputs. More than one bank record of one type per reference, or more than one ledger
/// TopUp debit per payment, is not expected; such rows are summed so the result stays deterministic.
/// </summary>
public static class Matcher
{
    private const string TopUpType = "TopUp";
    private const string CompletedStatus = "Completed";
    private const string CompensatedStatus = "Compensated";

    public static ReconciliationResult Match(
        IReadOnlyList<BankRecord> bank, IReadOnlyList<StagedPayment> payments, IReadOnlyList<StagedLedgerLine> ledger, DateOnly businessDate)
    {
        var start = new DateTimeOffset(businessDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = start.AddDays(1);
        var latest = LatestPerPayment(payments);
        var eligible = latest.Where(p => IsEligible(p) && InWindow(p.OccurredAt, start, end)).ToList();

        var referenceItems = MatchReferences(
            [.. bank.Where(b => InWindow(b.OccurredAt, start, end))],
            [.. ledger.Where(l => InWindow(l.PostedAt, start, end))],
            latest, eligible);
        var mismatches = referenceItems.Concat(TrialBalance(ledger, end)).OrderBy(m => m, MismatchOrder.Instance).ToList();

        var flagged = referenceItems.Select(m => m.Reference).ToHashSet(StringComparer.Ordinal);
        var matched = eligible.Where(p => !flagged.Contains(p.PaymentId.ToString())).Select(p => p.PaymentId).Order().ToList();
        return new ReconciliationResult(matched, mismatches);
    }

    /// <summary>
    /// One row per payment id. Staging's primary key makes duplicates unexpected; if one shows up anyway, keep the row with
    /// the latest OccurredAt (ties broken by status, ordinal) so the result stays deterministic.
    /// </summary>
    private static List<StagedPayment> LatestPerPayment(IReadOnlyList<StagedPayment> payments) =>
    [
        .. payments.GroupBy(p => p.PaymentId).Select(g => g
            .OrderByDescending(p => p.OccurredAt).ThenByDescending(p => p.Status, StringComparer.Ordinal).First()),
    ];

    private static bool IsEligible(StagedPayment payment) =>
        payment.Kind == TopUpType && payment.Status is CompletedStatus or CompensatedStatus;

    private static bool InWindow(DateTimeOffset moment, DateTimeOffset start, DateTimeOffset end) => moment >= start && moment < end;

    private static List<Mismatch> MatchReferences(
        List<BankRecord> bank, List<StagedLedgerLine> ledger, List<StagedPayment> allPayments, List<StagedPayment> eligible)
    {
        var known = allPayments.Select(p => p.PaymentId).ToHashSet();
        var unknownBank = bank.Where(b => !Guid.TryParse(b.Reference, out var id) || !known.Contains(id)).ToList();
        var items = unknownBank.Select(b => new Mismatch(MismatchCode.UnknownBankRecord, Key(b.Reference), b.AmountMinor, null, b.Currency)).ToList();

        var unknownKeys = unknownBank.Select(b => Key(b.Reference)).ToHashSet(StringComparer.Ordinal);
        var knownBank = bank.Where(b => !unknownKeys.Contains(Key(b.Reference))).ToList();
        var day = new Day(
            Sum(knownBank.Where(b => b.Type == BankRecordType.Capture), b => Key(b.Reference), b => b.AmountMinor, b => b.Currency),
            Sum(knownBank.Where(b => b.Type == BankRecordType.Refund), b => Key(b.Reference), b => b.AmountMinor, b => b.Currency),
            Sum(ledger.Where(IsClearingTopUpDebit), l => l.PaymentId.ToString(), l => l.AmountMinor, l => l.Currency),
            eligible.ToDictionary(p => p.PaymentId.ToString(), StringComparer.Ordinal),
            // The reverse half of PaymentLedgerMismatch uses the in-window Completed set (R1, R2). A top-up that straddles
            // midnight (ledger line before, payment completed after) reports on both days by design: the window is the UTC day.
            eligible.Where(p => p.Status == CompletedStatus).Select(p => p.PaymentId.ToString()).ToHashSet(StringComparer.Ordinal));

        var keys = day.Captures.Keys.Concat(day.Refunds.Keys).Concat(day.Ledger.Keys).Concat(day.Payments.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            // A reference with an unknown bank row reports only UnknownBankRecord (R4); its other rows are not examined.
            if (!unknownKeys.Contains(key)) items.AddRange(Check(key, day));
        }
        return items;
    }

    private static bool IsClearingTopUpDebit(StagedLedgerLine line) =>
        line.IsBankClearing && line.EntryType == TopUpType && line.Direction == "Debit";

    private static IEnumerable<Mismatch> Check(string key, Day day)
    {
        var capture = Find(day.Captures, key);
        var refund = Find(day.Refunds, key);
        var ledger = Find(day.Ledger, key);
        day.Payments.TryGetValue(key, out var payment);
        var compensated = payment?.Status == CompensatedStatus;

        // A compensated top-up with a capture and no refund is a missing refund, not a missing ledger entry.
        if (capture is { } c1 && ledger is null && refund is null && !compensated)
            yield return new Mismatch(MismatchCode.BankCaptureNotInLedger, key, c1.Minor, null, c1.Currency);
        if (ledger is { } l1 && capture is null)
            yield return new Mismatch(MismatchCode.LedgerEntryNotAtBank, key, null, l1.Minor, l1.Currency);
        if (capture is { } c2 && ledger is { } l2 && c2 != l2)
            yield return new Mismatch(MismatchCode.AmountMismatch, key, c2.Minor, l2.Minor, c2.Currency);
        if (payment?.Status == CompletedStatus && ledger is null)
            yield return new Mismatch(MismatchCode.PaymentLedgerMismatch, key, null, null, payment.Currency);
        if (compensated && capture is { } c3 && refund is null)
            yield return new Mismatch(MismatchCode.RefundMissingAtBank, key, c3.Minor, null, c3.Currency);
        foreach (var item in LedgerOnlyChecks(key, day)) yield return item;
    }

    /// <summary>The reverse half of PaymentLedgerMismatch: a ledger TopUp debit whose payment is not Completed.</summary>
    private static IEnumerable<Mismatch> LedgerOnlyChecks(string key, Day day)
    {
        if (Find(day.Ledger, key) is { } ledger && !day.CompletedKeys.Contains(key))
            yield return new Mismatch(MismatchCode.PaymentLedgerMismatch, key, null, ledger.Minor, ledger.Currency);
    }

    /// <summary>Step 6: per currency, credits minus debits of every staged line posted before the end of the business date.</summary>
    private static IEnumerable<Mismatch> TrialBalance(IReadOnlyList<StagedLedgerLine> ledger, DateTimeOffset end)
    {
        var balances = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in ledger.Where(l => l.PostedAt < end))
        {
            var signed = line.Direction switch
            {
                "Credit" => line.AmountMinor,
                "Debit" => checked(-line.AmountMinor),
                _ => throw new InvalidOperationException($"Unknown line direction '{line.Direction}'."),
            };
            balances[line.Currency] = checked(balances.GetValueOrDefault(line.Currency) + signed);
        }
        return balances.Where(b => b.Value != 0).Select(b => new Mismatch(MismatchCode.LedgerImbalance, b.Key, null, b.Value, b.Key));
    }

    /// <summary>Groups rows by reference and sums the amounts. The currency of a group is its smallest code, so it is stable.</summary>
    private static Dictionary<string, Amount> Sum<T>(
        IEnumerable<T> rows, Func<T, string> key, Func<T, long> minor, Func<T, string> currency) =>
        rows.GroupBy(key, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => new Amount(g.Aggregate(0L, (total, row) => checked(total + minor(row))), g.Select(currency).Min(StringComparer.Ordinal)!),
            StringComparer.Ordinal);

    /// <summary>The payment reference of a bank row: a GUID in its canonical form, otherwise the raw text.</summary>
    private static string Key(string reference) => Guid.TryParse(reference, out var id) ? id.ToString() : reference;

    private static Amount? Find(Dictionary<string, Amount> side, string key) => side.TryGetValue(key, out var amount) ? amount : null;

    private readonly record struct Amount(long Minor, string Currency);

    private sealed record Day(
        Dictionary<string, Amount> Captures, Dictionary<string, Amount> Refunds, Dictionary<string, Amount> Ledger,
        Dictionary<string, StagedPayment> Payments, HashSet<string> CompletedKeys);

    private sealed class MismatchOrder : IComparer<Mismatch>
    {
        public static readonly MismatchOrder Instance = new();

        public int Compare(Mismatch? x, Mismatch? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var byCode = x.Code.CompareTo(y.Code);
            if (byCode != 0) return byCode;
            var byReference = string.CompareOrdinal(x.Reference, y.Reference);
            if (byReference != 0) return byReference;
            var byBank = Comparer<long?>.Default.Compare(x.BankAmountMinor, y.BankAmountMinor);
            if (byBank != 0) return byBank;
            var byLedger = Comparer<long?>.Default.Compare(x.LedgerAmountMinor, y.LedgerAmountMinor);
            return byLedger != 0 ? byLedger : string.CompareOrdinal(x.Currency, y.Currency);
        }
    }
}
