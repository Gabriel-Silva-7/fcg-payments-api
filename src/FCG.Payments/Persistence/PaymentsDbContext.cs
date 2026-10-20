using Microsoft.EntityFrameworkCore;

namespace FCG.Payments.Persistence;

/// <summary>Caller-configured storage; BeginTransaction/UseTransaction permit atomic associated writes.</summary>
public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PersistedPaymentDecision> Decisions => Set<PersistedPaymentDecision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new PaymentDecisionConfiguration());
    }
}
