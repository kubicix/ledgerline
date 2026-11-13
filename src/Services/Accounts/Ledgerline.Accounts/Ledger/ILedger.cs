namespace Ledgerline.Accounts.Ledger;

public interface ILedger
{
    Task<PostingResult> PostAsync(PostingRequest request, CancellationToken ct);
}
