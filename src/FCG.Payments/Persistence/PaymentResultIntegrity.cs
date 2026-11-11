using System.Text.Json;
using FCG.Contracts;
namespace FCG.Payments.Persistence;

internal static class PaymentResultIntegrity
{
    public static PaymentProcessedEvent? Read(PersistedPaymentDecision decision, PaymentResult result)
    {
        PaymentProcessedEvent? message;
        try { message = JsonSerializer.Deserialize<PaymentProcessedEvent>(result.Payload); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return null; }
        return message is not null && result.OrderId == decision.OrderId && message.OrderId == decision.OrderId
            && message.UserId == decision.UserId && message.GameId == decision.GameId && message.Price == decision.Price
            && message.Status == decision.Status.ToString() && message.Reason == decision.Reason && message.ProcessedAtUtc == decision.ProcessedAtUtc
            ? message : null;
    }
}
