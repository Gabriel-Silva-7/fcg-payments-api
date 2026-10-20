namespace FCG.Payments.Persistence;

/// <summary>Integrity conflict for an existing order; the original decision remains authoritative.</summary>
public sealed class PaymentDecisionConflictException(Guid orderId, IReadOnlyList<string> differingFields)
    : Exception($"Payment request conflicts with the original decision for order {orderId}: {string.Join(", ", differingFields)}.")
{
    public Guid OrderId { get; } = orderId;
    public IReadOnlyList<string> DifferingFields { get; } = differingFields;
}
