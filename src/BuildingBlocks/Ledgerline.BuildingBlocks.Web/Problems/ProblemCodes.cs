namespace Ledgerline.BuildingBlocks.Web.Problems;

public static class ProblemCodes
{
    public const string ValidationFailed = "validation-failed";
    public const string IdempotencyKeyMissing = "idempotency-key-missing";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string InvalidState = "invalid-state";
    public const string RequestInProgress = "request-in-progress";
    public const string IdempotencyKeyReused = "idempotency-key-reused";
    public const string RateLimited = "rate-limited";

    internal static string ForStatus(int status) => status switch
    {
        400 => ValidationFailed, 401 => Unauthenticated, 403 => Forbidden, 404 => NotFound,
        409 => InvalidState, 422 => IdempotencyKeyReused, 429 => RateLimited, _ => "unexpected-error"
    };
}
