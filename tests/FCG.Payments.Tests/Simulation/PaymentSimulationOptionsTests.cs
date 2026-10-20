using FCG.Payments.Simulation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FCG.Payments.Tests.Simulation;

public sealed class PaymentSimulationOptionsTests
{
    [Fact]
    public void Defaults_are_500_brl_and_two_seconds()
    {
        var options = new PaymentSimulationOptions();
        var policy = options.CreatePolicy();
        Assert.Equal("PaymentSimulation", PaymentSimulationOptions.SectionName);
        Assert.Equal(500m, policy.ApprovalLimit);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Delay);
        Assert.True(new PaymentSimulationOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Documented_configuration_keys_bind_to_the_policy()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaymentSimulation:ApprovalLimit"] = "42.50",
            ["PaymentSimulation:Delay"] = "00:00:00"
        }).Build();
        var options = configuration.GetSection(PaymentSimulationOptions.SectionName).Get<PaymentSimulationOptions>();
        Assert.NotNull(options);
        var policy = options.CreatePolicy();
        Assert.Equal(42.50m, policy.ApprovalLimit);
        Assert.Equal(TimeSpan.Zero, policy.Delay);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("10000000000000000.00")]
    [InlineData("79228162514264337593543950335")]
    public void Invalid_limit_is_a_configuration_error(string value)
    {
        var limit = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaymentSimulationPolicy(limit));
        var options = new PaymentSimulationOptions { ApprovalLimit = limit };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreatePolicy());
        var validation = new PaymentSimulationOptionsValidator().Validate(null, options);
        Assert.True(validation.Failed);
        Assert.Contains(validation.Failures, message => message.Contains("PaymentSimulation:ApprovalLimit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("500.000")]
    [InlineData("9999999999999999.99")]
    public void Representable_limits_are_valid(string value)
    {
        var options = new PaymentSimulationOptions { ApprovalLimit = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.True(new PaymentSimulationOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), options.CreatePolicy().ApprovalLimit);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(3000000001L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Invalid_delay_is_a_configuration_error(long ticks)
    {
        var delay = TimeSpan.FromTicks(ticks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PaymentSimulationPolicy(delay: delay));
        var options = new PaymentSimulationOptions { Delay = delay };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreatePolicy());
        var validation = new PaymentSimulationOptionsValidator().Validate(null, options);
        Assert.True(validation.Failed);
        Assert.Contains(validation.Failures, message => message.Contains("PaymentSimulation:Delay", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(20000000L)]
    [InlineData(3000000000L)]
    public void Delays_including_both_operational_boundaries_are_valid(long ticks)
    {
        var options = new PaymentSimulationOptions { Delay = TimeSpan.FromTicks(ticks) };
        Assert.True(new PaymentSimulationOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(TimeSpan.FromTicks(ticks), options.CreatePolicy().Delay);
    }

    [Fact]
    public void Validator_reports_both_invalid_keys()
    {
        var validation = new PaymentSimulationOptionsValidator().Validate(null,
            new PaymentSimulationOptions { ApprovalLimit = -1m, Delay = TimeSpan.FromTicks(-1) });
        Assert.True(validation.Failed);
        Assert.Equal(2, validation.Failures.Count());
        Assert.Contains(validation.Failures, message => message.Contains("PaymentSimulation:ApprovalLimit", StringComparison.Ordinal));
        Assert.Contains(validation.Failures, message => message.Contains("PaymentSimulation:Delay", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_null_options_explicitly() =>
        Assert.Throws<ArgumentNullException>(() => new PaymentSimulationOptionsValidator().Validate(null, null!));
}
