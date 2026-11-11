using Microsoft.Extensions.Options;

namespace FCG.Payments.Simulation;

public sealed record PaymentSimulationOptions
{
    public const string SectionName = "PaymentSimulation";
    // Keep configuration text intact until cent precision and range are checked.
    public string ApprovalLimit { get; init; } = "500";
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    public PaymentSimulationPolicy CreatePolicy()
    {
        if (!PaymentSimulationValidation.TryParseConfiguredAmount(ApprovalLimit, out var limit))
            throw new ArgumentOutOfRangeException(nameof(ApprovalLimit), "Approval limit must be an exact non-negative numeric(18,2) BRL amount in invariant decimal notation.");
        return new(limit, Delay);
    }
}

/// <summary>Register with options validation and ValidateOnStart when the processing host is connected.</summary>
public sealed class PaymentSimulationOptionsValidator : IValidateOptions<PaymentSimulationOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentSimulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];
        if (!PaymentSimulationValidation.TryParseConfiguredAmount(options.ApprovalLimit, out _))
            errors.Add("PaymentSimulation:ApprovalLimit must fit non-negative numeric(18,2) BRL without rounding.");
        if (!PaymentSimulationValidation.IsDelay(options.Delay))
            errors.Add("PaymentSimulation:Delay must be between 00:00:00 and 00:05:00, inclusive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
