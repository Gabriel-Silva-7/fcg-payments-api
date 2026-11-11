using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FCG.Payments.Persistence;

public sealed class PaymentsStorageHealthCheck(PaymentsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.PaymentRecoveryAttempts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Decisions.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Results.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.MessageConflicts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Set<InboxState>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Set<OutboxState>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Set<OutboxMessage>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { return HealthCheckResult.Unhealthy("Payments storage is unavailable."); }
    }
}
