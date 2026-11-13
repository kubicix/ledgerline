namespace Ledgerline.Risk.Features.Evaluate;

public interface IVelocityCounter
{
    Task<int> CountAsync(Guid customerId, Guid paymentId, DateTimeOffset at, int windowSeconds, CancellationToken ct);
}
