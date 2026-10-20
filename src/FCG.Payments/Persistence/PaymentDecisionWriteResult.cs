namespace FCG.Payments.Persistence;

/// <summary>Created is provisional until the caller commits an external transaction.</summary>
public sealed record PaymentDecisionWriteResult(PersistedPaymentDecision Decision, bool Created);
