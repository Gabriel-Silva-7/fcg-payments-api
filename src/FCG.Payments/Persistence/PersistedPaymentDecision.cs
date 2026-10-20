using FCG.Payments.Simulation;

namespace FCG.Payments.Persistence;

/// <summary>The original request, result and applied policy; no public mutation path.</summary>
public sealed class PersistedPaymentDecision
{
    private PersistedPaymentDecision() { }

    internal PersistedPaymentDecision(PaymentSimulationDecision decision)
    {
        OrderId = decision.OrderId;
        UserId = decision.UserId;
        GameId = decision.GameId;
        Price = decision.Price;
        PlacedAtUtc = decision.PlacedAtUtc;
        Status = decision.Status;
        Reason = decision.Reason;
        ProcessedAtUtc = decision.ProcessedAtUtc;
        ApprovalLimit = decision.AppliedPolicy.ApprovalLimit;
        DelayTicks = decision.AppliedPolicy.Delay.Ticks;
        PolicyVersion = "amount-limit-v1";
    }

    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid GameId { get; private set; }
    public decimal Price { get; private set; }
    public DateTimeOffset PlacedAtUtc { get; private set; }
    public PaymentSimulationStatus Status { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset ProcessedAtUtc { get; private set; }
    public decimal ApprovalLimit { get; private set; }
    public long DelayTicks { get; private set; }
    public string PolicyVersion { get; private set; } = null!;
}
