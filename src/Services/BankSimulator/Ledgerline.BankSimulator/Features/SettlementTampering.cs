using System.Collections.Concurrent;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Problems;

namespace Ledgerline.BankSimulator.Features;

public sealed record SettlementTamperingRequest(DateOnly Date, string? DropCaptureId, string? AlterCaptureId, long? AlteredAmountMinor);

public sealed class SettlementTampering
{
    private readonly ConcurrentDictionary<DateOnly, SettlementTamperingRequest> definitions = new();
    public SettlementTamperingRequest? For(DateOnly date) => definitions.GetValueOrDefault(date);

    public IResult Set(SettlementTamperingRequest request)
    {
        if ((request.AlterCaptureId is null) != (request.AlteredAmountMinor is null) || request.AlteredAmountMinor is < 0 or > Money.MaxAmountMinor)
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["tampering"] = ["An altered capture requires an ID and an amount within range."] });
        definitions[request.Date] = request;
        return Results.NoContent();
    }
}
