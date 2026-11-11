namespace FCG.Payments.Simulation;

public sealed record PaymentSimulationRequest(Guid OrderId, Guid UserId, Guid GameId, decimal Price, DateTimeOffset PlacedAtUtc);
