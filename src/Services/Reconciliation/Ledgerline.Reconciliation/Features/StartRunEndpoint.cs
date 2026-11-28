using System.Globalization;
using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Web.Auth;
using Ledgerline.BuildingBlocks.Web.Problems;
using Ledgerline.Reconciliation.Runs;

namespace Ledgerline.Reconciliation.Features;

/// <summary>The admin API of reconciliation.md section 5. The gateway strips <c>/api/reconciliation</c>.</summary>
public static class ReconciliationEndpoints
{
    public static WebApplication MapReconciliationEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // No Idempotency-Key on POST /runs: starting a run is idempotent by business date (the unique date and step 2 of the run).
        app.MapPost("/runs", StartRunEndpoint.HandleAsync).RequireAuthorization(Policies.Admin);
        app.MapGet("/runs", ListRunsEndpoint.HandleAsync).RequireAuthorization(Policies.Admin);
        app.MapGet("/runs/{businessDate}", GetRunEndpoint.HandleAsync).RequireAuthorization(Policies.Admin);
        app.MapGet("/runs/{businessDate}/items", ListItemsEndpoint.HandleAsync).RequireAuthorization(Policies.Admin);
        return app;
    }
}

/// <summary>Business dates travel as <c>yyyy-MM-dd</c> text, parsed here so a malformed one is always a <c>validation-failed</c> problem.</summary>
internal static class BusinessDates
{
    private const string Format = "yyyy-MM-dd";

    internal static bool TryParse(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal static string ToText(DateOnly date) => date.ToString(Format, CultureInfo.InvariantCulture);

    internal static IResult Invalid(string field, string detail) => ProblemResults.Validation(new Dictionary<string, string[]> { [field] = [detail] });
}

public sealed record StartRunRequest(string? BusinessDate);

public sealed record StartRunResponse(string BusinessDate);

internal static class StartRunEndpoint
{
    internal static async Task<IResult> HandleAsync(StartRunRequest? request, IRunTrigger trigger, IClock clock, CancellationToken ct)
    {
        if (!BusinessDates.TryParse(request?.BusinessDate, out var date)) return BusinessDates.Invalid("businessDate", "Use a business date as yyyy-MM-dd.");
        if (date > DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)) return BusinessDates.Invalid("businessDate", "A business date must not be in the future.");
        // Always accepted, a Completed date included: the run then exits at step 2, or re-runs a date whose inputs were incomplete.
        await trigger.TriggerAsync(date, ct);
        var text = BusinessDates.ToText(date);
        return Results.Accepted("/runs/" + text, new StartRunResponse(text));
    }
}
