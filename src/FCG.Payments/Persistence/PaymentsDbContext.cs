using Microsoft.EntityFrameworkCore;
using MassTransit;

namespace FCG.Payments.Persistence;

/// <summary>Caller-configured storage; BeginTransaction/UseTransaction permit atomic associated writes.</summary>
public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRecoveryAttempt> PaymentRecoveryAttempts => Set<PaymentRecoveryAttempt>();
    public DbSet<PaymentResult> Results => Set<PaymentResult>();
    public DbSet<MessageConflict> MessageConflicts => Set<MessageConflict>();
    public DbSet<PersistedPaymentDecision> Decisions => Set<PersistedPaymentDecision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new PaymentDecisionConfiguration());
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.Entity<PaymentRecoveryAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Outcome).HasMaxLength(40);
            entity.Property(x => x.Reason).HasMaxLength(80);
            entity.HasOne<MessageConflict>().WithMany().HasForeignKey(x => x.ConflictId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ConflictId, x.AttemptedAtUtc });
        });
        modelBuilder.Entity<PaymentResult>(entity =>
        {
            entity.HasKey(x => x.OrderId);
            entity.Property(x => x.Payload).IsRequired();
            entity.HasOne<PersistedPaymentDecision>().WithOne().HasForeignKey<PaymentResult>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MessageConflict>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BusinessId, x.PayloadHash }).IsUnique();
            entity.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.MessageType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        });
    }
}
