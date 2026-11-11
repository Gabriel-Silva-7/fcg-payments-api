namespace FCG.Payments.Simulation;

/// <summary>Validated BRL policy snapshot; delay is metadata for the processing adapter.</summary>
public sealed class PaymentSimulationPolicy
{
    public PaymentSimulationPolicy(decimal approvalLimit = 500m, TimeSpan? delay = null)
    {
        if (!PaymentSimulationValidation.IsAmount(approvalLimit))
            throw new ArgumentOutOfRangeException(nameof(approvalLimit), "Approval limit must fit non-negative numeric(18,2) BRL without rounding.");

        var actualDelay = delay ?? TimeSpan.FromSeconds(2);
        if (!PaymentSimulationValidation.IsDelay(actualDelay))
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be between zero and five minutes, inclusive.");

        ApprovalLimit = approvalLimit;
        Delay = actualDelay;
    }

    public decimal ApprovalLimit { get; }
    public TimeSpan Delay { get; }
}
