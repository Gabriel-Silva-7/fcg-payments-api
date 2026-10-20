using System.Globalization;

namespace FCG.Payments.Simulation;

internal static class PaymentSimulationValidation
{
    internal const decimal MaximumAmount = 9999999999999999.99m;
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    internal static bool IsAmount(decimal value) =>
        value >= 0 && value <= MaximumAmount && decimal.Round(value, 2) == value;

    // Bound significant decimal digits before conversion; decimal.TryParse alone can round.
    internal static bool TryParseConfiguredAmount(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var raw = text.AsSpan();
        var point = raw.IndexOf('.');
        var whole = point < 0 ? raw : raw[..point];
        var fraction = point < 0 ? ReadOnlySpan<char>.Empty : raw[(point + 1)..];
        if (whole.IsEmpty || (point >= 0 && fraction.IsEmpty)) return false;
        foreach (var digit in whole)
            if (!char.IsAsciiDigit(digit)) return false;
        foreach (var digit in fraction)
            if (!char.IsAsciiDigit(digit)) return false;
        whole = whole.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        if (whole.Length > 16 || fraction.Length > 2) return false;
        var integer = whole.IsEmpty ? "0" : whole.ToString();
        var exact = fraction.IsEmpty ? integer : integer + "." + fraction.ToString();
        return decimal.TryParse(exact, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && IsAmount(value);
    }

    internal static bool IsDelay(TimeSpan value) => value >= TimeSpan.Zero && value <= MaximumDelay;
}
