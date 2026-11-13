using Ledgerline.Risk.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Risk.Features.Evaluate;

public sealed class DatabaseVelocityCounter(RiskDbContext db) : IVelocityCounter
{
    public async Task<int> CountAsync(Guid customerId, Guid paymentId, DateTimeOffset at, int windowSeconds, CancellationToken ct) =>
        1 + await db.Decisions.CountAsync(x => x.CustomerId == customerId && x.PaymentId != paymentId &&
            x.EvaluatedAt > at.AddSeconds(-windowSeconds), ct);
}
