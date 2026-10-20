namespace FCG.Payments.Simulation;

internal static class PaymentSimulationValidation
{
    internal const decimal MaximumAmount = 9999999999999999.99m;
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    internal static bool IsAmount(decimal value) =>
        value >= 0 && value <= MaximumAmount && decimal.Round(value, 2) == value;

    internal static bool IsDelay(TimeSpan value) => value >= TimeSpan.Zero && value <= MaximumDelay;
}
