namespace FCG.Payments.Simulation;

public enum PaymentSimulationStatus { Approved, Rejected }

/// <summary>A calculation snapshot; persistence and transport belong to the caller.</summary>
public sealed class PaymentSimulationDecision
{
    internal PaymentSimulationDecision(PaymentSimulationRequest request, PaymentSimulationPolicy policy, DateTimeOffset processedAtUtc)
    {
        OrderId = request.OrderId;
        UserId = request.UserId;
        GameId = request.GameId;
        Price = request.Price;
        PlacedAtUtc = request.PlacedAtUtc;
        AppliedPolicy = policy;
        ProcessedAtUtc = processedAtUtc;
        Status = request.Price <= policy.ApprovalLimit ? PaymentSimulationStatus.Approved : PaymentSimulationStatus.Rejected;
        Reason = Status == PaymentSimulationStatus.Rejected ? "AmountAboveLimit" : null;
    }

    public Guid OrderId { get; }
    public Guid UserId { get; }
    public Guid GameId { get; }
    public decimal Price { get; }
    public DateTimeOffset PlacedAtUtc { get; }
    public PaymentSimulationStatus Status { get; }
    public string? Reason { get; }
    public DateTimeOffset ProcessedAtUtc { get; }
    public PaymentSimulationPolicy AppliedPolicy { get; }
}
