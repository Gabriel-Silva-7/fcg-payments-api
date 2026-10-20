using FCG.Payments.Simulation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FCG.Payments.Tests.Simulation;

public sealed class PaymentSimulationConfigurationTests
{
    [Theory]
    [InlineData("0.000000000000000000000000000001")]
    [InlineData("-0.000000000000000000000000000001")]
    [InlineData("500.00000000000000000000000000001")]
    public void Raw_configuration_cannot_round_an_invalid_limit_into_a_valid_policy(string raw)
    {
        var options = Bind(raw);
        var validation = new PaymentSimulationOptionsValidator().Validate(null, options);
        Assert.True(validation.Failed);
        Assert.Contains(validation.Failures, error => error.Contains("PaymentSimulation:ApprovalLimit", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreatePolicy());
    }

    [Theory]
    [InlineData("0.000000000000000000000000000001")]
    [InlineData("-0.000000000000000000000000000001")]
    [InlineData("500.00000000000000000000000000001")]
    public void Json_numeric_configuration_also_preserves_invalid_precision(string raw)
    {
        var json = "{\"PaymentSimulation\":{\"ApprovalLimit\":" + raw + ",\"Delay\":\"00:00:00\"}}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var options = new ConfigurationBuilder().AddJsonStream(stream).Build()
            .GetSection(PaymentSimulationOptions.SectionName).Get<PaymentSimulationOptions>()!;
        Assert.Equal(raw, options.ApprovalLimit);
        Assert.True(new PaymentSimulationOptionsValidator().Validate(null, options).Failed);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreatePolicy());
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("500.00", "500")]
    [InlineData("500.000", "500")]
    [InlineData("000500.000", "500")]
    [InlineData("42.50", "42.5")]
    [InlineData("9999999999999999.99", "9999999999999999.99")]
    [InlineData("0.00000000000000000000000000000000000", "0")]
    [InlineData("9999999999999999.99000000000000000000000000000000000", "9999999999999999.99")]
    public void Exact_valid_configuration_preserves_value_despite_redundant_zeros(string raw, string expected)
    {
        var options = Bind(raw);
        Assert.Equal(raw, options.ApprovalLimit);
        Assert.True(new PaymentSimulationOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), options.CreatePolicy().ApprovalLimit);
        Assert.Equal(TimeSpan.Zero, options.CreatePolicy().Delay);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+500")]
    [InlineData("-0")]
    [InlineData("500,00")]
    [InlineData("1e2")]
    [InlineData(".50")]
    [InlineData("500.")]
    [InlineData("1.2.3")]
    [InlineData("٥٠٠")]
    [InlineData("500 00")]
    [InlineData("500\n")]
    [InlineData("0.001")]
    [InlineData("-0.01")]
    [InlineData("10000000000000000.00")]
    [InlineData("9999999999999999.991")]
    [InlineData("79228162514264337593543950335")]
    public void Invalid_raw_format_or_amount_fails_configuration_instead_of_creating_policy(string raw)
    {
        var options = Bind(raw);
        var validation = new PaymentSimulationOptionsValidator().Validate(null, options);
        Assert.True(validation.Failed);
        Assert.Contains(validation.Failures, error => error.Contains("PaymentSimulation:ApprovalLimit", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreatePolicy());
    }

    [Fact]
    public void Missing_limit_uses_default_while_other_configuration_still_binds()
    {
        var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaymentSimulation:Delay"] = "00:00:00"
        }).Build().GetSection(PaymentSimulationOptions.SectionName).Get<PaymentSimulationOptions>()!;
        Assert.Equal(500m, options.CreatePolicy().ApprovalLimit);
        Assert.Equal(TimeSpan.Zero, options.CreatePolicy().Delay);
    }

    [Fact]
    public void Null_text_in_options_is_invalid()
    {
        var options = new PaymentSimulationOptions { ApprovalLimit = null! };
        Assert.True(new PaymentSimulationOptionsValidator().Validate(null, options).Failed);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreatePolicy());
    }

    private static PaymentSimulationOptions Bind(string raw) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaymentSimulation:ApprovalLimit"] = raw,
            ["PaymentSimulation:Delay"] = "00:00:00"
        }).Build().GetSection(PaymentSimulationOptions.SectionName).Get<PaymentSimulationOptions>()!;
}
