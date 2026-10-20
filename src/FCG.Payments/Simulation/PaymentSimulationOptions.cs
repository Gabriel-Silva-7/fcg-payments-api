using Microsoft.Extensions.Options;

namespace FCG.Payments.Simulation;

public sealed record PaymentSimulationOptions
{
    public const string SectionName = "PaymentSimulation";
    public decimal ApprovalLimit { get; init; } = 500m;
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    public PaymentSimulationPolicy CreatePolicy() => new(ApprovalLimit, Delay);
}

/// <summary>Register with options validation and ValidateOnStart when the processing host is connected.</summary>
public sealed class PaymentSimulationOptionsValidator : IValidateOptions<PaymentSimulationOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentSimulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];
        if (!PaymentSimulationValidation.IsAmount(options.ApprovalLimit))
            errors.Add("PaymentSimulation:ApprovalLimit must fit non-negative numeric(18,2) BRL without rounding.");
        if (!PaymentSimulationValidation.IsDelay(options.Delay))
            errors.Add("PaymentSimulation:Delay must be between 00:00:00 and 00:05:00, inclusive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
