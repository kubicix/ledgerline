using Ledgerline.BuildingBlocks;
using Ledgerline.BuildingBlocks.Messaging;
using Ledgerline.Contracts.Risk;
using Ledgerline.Risk.Domain;
using Ledgerline.Risk.Infrastructure;
using Ledgerline.Risk.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ledgerline.Risk.Features.Evaluate;

public sealed class EvaluateRiskHandler(RiskDbContext db, RuleCache cache, IVelocityCounter velocity,
    IOutbox outbox, IClock clock) : IMessageHandler<EvaluateRisk>
{
    public async Task HandleAsync(EvaluateRisk message, MessageContext context, CancellationToken ct)
    {
        var existing = await db.Decisions.FindAsync([message.PaymentId], ct);
        if (existing is not null) { Reply(existing, context); return; }
        // Velocity is customer-wide, including different currencies and requested days.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"risk:" + message.CustomerId}, 0))", ct);
        var day = DateOnly.FromDateTime(message.RequestedAt.UtcDateTime);
        var total = await LockTotalAsync(message.CustomerId, day, message.Currency, ct);
        existing = await db.Decisions.SingleOrDefaultAsync(x => x.PaymentId == message.PaymentId, ct);
        if (existing is not null) { Reply(existing, context); return; }
        var rules = await cache.GetAsync(db, ct);
        var window = rules.Where(x => x.Code == RuleCode.VelocityLimit).Select(x => x.WindowSeconds!.Value).DefaultIfEmpty(60).Max();
        var count = await velocity.CountAsync(message.CustomerId, message.PaymentId, message.RequestedAt, window, ct);
        var request = new RiskRequest(message.PaymentId, message.Kind, message.CustomerId, message.AccountId,
            message.AmountMinor, message.Currency, message.RequestedAt);
        var result = RiskEvaluator.Evaluate(rules, request, new(total.TotalMinor, count));
        if (result.Approved) total.TotalMinor = checked(total.TotalMinor + message.AmountMinor);
        var decision = new RiskDecision
        {
            PaymentId = message.PaymentId, CustomerId = message.CustomerId, Kind = message.Kind,
            AmountMinor = message.AmountMinor, Currency = message.Currency, Outcome = result.Approved ? "Approved" : "Rejected",
            RuleCodes = result.RuleCodes.ToArray(), EvaluatedAt = new DateTimeOffset(clock.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero),
            RequestedAt = DateTimeOffset.FromUnixTimeMilliseconds(message.RequestedAt.ToUnixTimeMilliseconds())
        };
        db.Decisions.Add(decision);
        Reply(decision, context);
    }

    private async Task<DailyTotal> LockTotalAsync(Guid customer, DateOnly day, string currency, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO daily_totals (customer_id, day, currency, total_minor)
            VALUES ({customer}, {day}, {currency}, 0) ON CONFLICT DO NOTHING
            """, ct);
        return await db.DailyTotals.FromSqlInterpolated($"""
            SELECT * FROM daily_totals WHERE customer_id = {customer} AND day = {day} AND currency = {currency} FOR UPDATE
            """).SingleAsync(ct);
    }

    private void Reply(RiskDecision decision, MessageContext context)
    {
        if (decision.Outcome == "Approved")
            outbox.Enqueue(new RiskApproved(decision.PaymentId, decision.EvaluatedAt), context.CorrelationId, context.MessageId);
        else outbox.Enqueue(new RiskRejected(decision.PaymentId, decision.RuleCodes, decision.EvaluatedAt), context.CorrelationId, context.MessageId);
    }
}
