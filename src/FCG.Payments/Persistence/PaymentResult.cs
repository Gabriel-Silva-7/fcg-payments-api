namespace FCG.Payments.Persistence;

public sealed class PaymentResult
{
    public Guid OrderId { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
