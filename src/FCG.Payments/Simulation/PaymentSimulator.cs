namespace FCG.Payments.Simulation;

/// <summary>Pure BRL simulation: no I/O, clock reads, delay, or durable deduplication.</summary>
public static class PaymentSimulator
{
    public static PaymentSimulationDecision Evaluate(PaymentSimulationRequest request, PaymentSimulationPolicy policy, DateTimeOffset processedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        RequireIdentity(request.OrderId, nameof(request.OrderId));
        RequireIdentity(request.UserId, nameof(request.UserId));
        RequireIdentity(request.GameId, nameof(request.GameId));
        if (!PaymentSimulationValidation.IsAmount(request.Price))
            throw new ArgumentOutOfRangeException(nameof(request.Price), "Price must fit non-negative numeric(18,2) BRL without rounding.");
        RequireUtcMicroseconds(request.PlacedAtUtc, nameof(request.PlacedAtUtc));
        RequireUtcMicroseconds(processedAtUtc, nameof(processedAtUtc));

        return new PaymentSimulationDecision(request, policy, processedAtUtc);
    }

    private static void RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Identity must not be empty.", parameterName);
    }

    private static void RequireUtcMicroseconds(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero || value.Ticks % 10 != 0)
            throw new ArgumentException("Timestamp must use UTC with at most microsecond precision.", parameterName);
    }
}
