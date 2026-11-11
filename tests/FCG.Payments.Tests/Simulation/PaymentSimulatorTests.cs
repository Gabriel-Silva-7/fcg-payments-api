using FCG.Payments.Simulation;
using Xunit;

namespace FCG.Payments.Tests.Simulation;

public sealed class PaymentSimulatorTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GameId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset PlacedAt = DateTimeOffset.Parse("2026-10-19T12:00:00.123456+00:00");
    private static readonly DateTimeOffset ProcessedAt = DateTimeOffset.Parse("2026-10-19T12:00:02.654321+00:00");

    [Theory]
    [InlineData("0", PaymentSimulationStatus.Approved, null)]
    [InlineData("500", PaymentSimulationStatus.Approved, null)]
    [InlineData("500.000", PaymentSimulationStatus.Approved, null)]
    [InlineData("500.01", PaymentSimulationStatus.Rejected, "AmountAboveLimit")]
    public void Default_policy_decides_and_preserves_order_snapshot(string price, PaymentSimulationStatus status, string? reason)
    {
        var result = PaymentSimulator.Evaluate(Request(Money(price)), new PaymentSimulationPolicy(), ProcessedAt);
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(OrderId, result.OrderId);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal(GameId, result.GameId);
        Assert.Equal(Money(price), result.Price);
        Assert.Equal(PlacedAt, result.PlacedAtUtc);
        Assert.Equal(ProcessedAt, result.ProcessedAtUtc);
        Assert.Equal(500m, result.AppliedPolicy.ApprovalLimit);
        Assert.Equal(TimeSpan.FromSeconds(2), result.AppliedPolicy.Delay);
    }

    [Theory]
    [InlineData("42.50", PaymentSimulationStatus.Approved, null)]
    [InlineData("42.51", PaymentSimulationStatus.Rejected, "AmountAboveLimit")]
    public void Custom_policy_decides_and_is_retained(string price, PaymentSimulationStatus status, string? reason)
    {
        var options = new PaymentSimulationOptions { ApprovalLimit = "42.50", Delay = TimeSpan.Zero };
        var result = PaymentSimulator.Evaluate(Request(Money(price)), options.CreatePolicy(), ProcessedAt);
        options = options with { ApprovalLimit = "900", Delay = TimeSpan.FromSeconds(9) };
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(42.50m, result.AppliedPolicy.ApprovalLimit);
        Assert.Equal(TimeSpan.Zero, result.AppliedPolicy.Delay);
        Assert.Equal("900", options.ApprovalLimit);
    }

    [Fact]
    public void Maximum_database_amount_is_approved_at_equal_limit()
    {
        var result = PaymentSimulator.Evaluate(Request(9999999999999999.99m), new PaymentSimulationPolicy(9999999999999999.99m), ProcessedAt);
        Assert.Equal(PaymentSimulationStatus.Approved, result.Status);
        Assert.Null(result.Reason);
        Assert.Equal(9999999999999999.99m, result.Price);
    }

    [Fact]
    public void Delay_is_metadata_and_a_five_minute_policy_returns_synchronously()
    {
        var result = PaymentSimulator.Evaluate(Request(0m), new PaymentSimulationPolicy(0m, TimeSpan.FromMinutes(5)), ProcessedAt);
        Assert.Equal(PaymentSimulationStatus.Approved, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), result.AppliedPolicy.Delay);
        Assert.Equal(ProcessedAt, result.ProcessedAtUtc);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("10000000000000000.00")]
    [InlineData("79228162514264337593543950335")]
    public void Invalid_price_fails_validation_instead_of_financial_rejection(string value)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => PaymentSimulator.Evaluate(Request(Money(value)), new PaymentSimulationPolicy(), ProcessedAt));
        Assert.Equal("Price", error.ParamName);
    }

    [Theory]
    [InlineData("OrderId")]
    [InlineData("UserId")]
    [InlineData("GameId")]
    public void Each_empty_identity_is_invalid(string field)
    {
        var request = Request(5m);
        request = field switch
        {
            "OrderId" => request with { OrderId = Guid.Empty },
            "UserId" => request with { UserId = Guid.Empty },
            _ => request with { GameId = Guid.Empty }
        };
        var error = Assert.Throws<ArgumentException>(() => PaymentSimulator.Evaluate(request, new PaymentSimulationPolicy(), ProcessedAt));
        Assert.Equal(field, error.ParamName);
    }

    [Theory]
    [InlineData("PlacedAtUtc", false)]
    [InlineData("PlacedAtUtc", true)]
    [InlineData("processedAtUtc", false)]
    [InlineData("processedAtUtc", true)]
    public void Timestamps_require_utc_microsecond_precision(string field, bool extraTick)
    {
        var invalid = extraTick ? PlacedAt.AddTicks(1) : PlacedAt.ToOffset(TimeSpan.FromHours(-3));
        var request = field == "PlacedAtUtc" ? Request(5m) with { PlacedAtUtc = invalid } : Request(5m);
        var processed = field == "processedAtUtc" ? invalid : ProcessedAt;
        var error = Assert.Throws<ArgumentException>(() => PaymentSimulator.Evaluate(request, new PaymentSimulationPolicy(), processed));
        Assert.Equal(field, error.ParamName);
    }

    [Fact]
    public void Null_arguments_fail_explicitly()
    {
        Assert.Throws<ArgumentNullException>(() => PaymentSimulator.Evaluate(null!, new PaymentSimulationPolicy(), ProcessedAt));
        Assert.Throws<ArgumentNullException>(() => PaymentSimulator.Evaluate(Request(0m), null!, ProcessedAt));
    }

    private static PaymentSimulationRequest Request(decimal price) => new(OrderId, UserId, GameId, price, PlacedAt);
    private static decimal Money(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
