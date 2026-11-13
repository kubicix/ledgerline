using Ledgerline.BuildingBlocks.Web.Problems;

namespace Ledgerline.BankSimulator.Features;

public static class FaultsEndpoint
{
    public static IResult Set(FaultDefinition fault, FaultState state)
    {
        if (fault.Operation is not ("Authorize" or "Capture" or "Void" or "Refund" or "Lookup" or "Any") ||
            fault.Mode is not ("Error500" or "Timeout" or "SuccessThenTimeout" or "Delay") || fault.Count <= 0 || fault.DelayMs is < 0)
            return ProblemResults.Validation(new Dictionary<string, string[]> { ["fault"] = ["Supply a known operation, mode, positive count and non-negative delay."] });
        state.Set(fault);
        return Results.NoContent();
    }

    public static IResult Clear(FaultState state)
    {
        state.Clear();
        return Results.NoContent();
    }
}
