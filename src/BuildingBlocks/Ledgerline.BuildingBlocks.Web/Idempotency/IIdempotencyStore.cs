namespace Ledgerline.BuildingBlocks.Web.Idempotency;

public interface IIdempotencyStore
{
    Task<IdempotencyBegin> TryBeginAsync(string scope, string key, string hash);
    Task CompleteAsync(string scope, string key, string hash, Guid resourceId);
    Task ReleaseAsync(string scope, string key);
}

public abstract record IdempotencyBegin
{
    public sealed record Started : IdempotencyBegin;
    public sealed record InProgress : IdempotencyBegin;
    public sealed record Done(Guid ResourceId) : IdempotencyBegin;
    public sealed record HashMismatch : IdempotencyBegin;
    public sealed record Unavailable : IdempotencyBegin;
}

public interface IIdempotentResource
{
    Guid Id { get; }
}
