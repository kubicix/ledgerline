using Microsoft.AspNetCore.Http;

namespace Ledgerline.BuildingBlocks.Web.Problems;

public static class ProblemResults
{
    public static IResult NotFound() => For(404, ProblemCodes.NotFound, "Resource not found", "The requested resource was not found.");
    public static IResult InvalidState(string detail) => For(409, ProblemCodes.InvalidState, "Invalid state", detail);
    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: "urn:ledgerline:problem:" + ProblemCodes.ValidationFailed);
    public static IResult IdempotencyKeyMissing() => For(400, ProblemCodes.IdempotencyKeyMissing,
        "Idempotency key required", "Supply a valid Idempotency-Key header.");
    public static IResult IdempotencyKeyReused() => For(422, ProblemCodes.IdempotencyKeyReused,
        "Idempotency key reused", "The idempotency key was used for a different request.");

    internal static IResult For(int status, string code, string title, string? detail = null) =>
        Results.Problem(statusCode: status, type: "urn:ledgerline:problem:" + code, title: title, detail: detail ?? title);
}
