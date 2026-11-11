using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Payments.Persistence;

internal sealed class PaymentDecisionConfiguration : IEntityTypeConfiguration<PersistedPaymentDecision>
{
    public void Configure(EntityTypeBuilder<PersistedPaymentDecision> entity)
    {
        entity.ToTable("payment_decisions", "public", table =>
        {
            table.HasCheckConstraint("CK_payment_decisions_identity", "order_id <> '00000000-0000-0000-0000-000000000000'::uuid AND user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND game_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("CK_payment_decisions_amounts", "price >= 0 AND approval_limit >= 0");
            table.HasCheckConstraint("CK_payment_decisions_delay", "delay_ticks >= 0 AND delay_ticks <= 3000000000");
            table.HasCheckConstraint("CK_payment_decisions_result", "(status = 'Approved' AND reason IS NULL AND price <= approval_limit) OR (status = 'Rejected' AND reason = 'AmountAboveLimit' AND reason IS NOT NULL AND price > approval_limit)");
        });
        entity.HasKey(value => value.OrderId).HasName("PK_payment_decisions");
        entity.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        entity.Property(value => value.UserId).HasColumnName("user_id");
        entity.Property(value => value.GameId).HasColumnName("game_id");
        entity.Property(value => value.Price).HasColumnName("price").HasPrecision(18, 2);
        entity.Property(value => value.PlacedAtUtc).HasColumnName("placed_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(value => value.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(8);
        entity.Property(value => value.Reason).HasColumnName("reason").HasMaxLength(16);
        entity.Property(value => value.ProcessedAtUtc).HasColumnName("processed_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(value => value.ApprovalLimit).HasColumnName("approval_limit").HasPrecision(18, 2);
        entity.Property(value => value.DelayTicks).HasColumnName("delay_ticks");
        entity.Property(value => value.PolicyVersion).HasColumnName("policy_version").HasMaxLength(32);
    }
}
