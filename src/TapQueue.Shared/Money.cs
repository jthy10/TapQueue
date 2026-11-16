using System.Globalization;

namespace TapQueue.Shared;

/// <summary>
/// Money in TapQueue is a whole number of thousandths of the currency's unit ("mills"): 0.05 is 50,
/// 12.50 is 12500. A page often costs a fraction of a cent, which a number of cents couldn't hold,
/// and whole numbers add up without rounding surprises.
/// </summary>
public static class Money
{
    public const long PerUnit = 1000;

    /// <summary>The most a price, a balance or an adjustment may be: a million units.</summary>
    public const long Max = 1_000_000 * PerUnit;

    /// <summary>
    /// "$1.25", "12.50 kr", "-$0.035". A currency made of letters (kr, EUR, CHF) goes after the
    /// amount; a symbol goes before it. Two decimals, or three when there are fractions of a cent.
    /// </summary>
    public static string Format(long mills, string? currency)
    {
        currency = string.IsNullOrWhiteSpace(currency) ? "" : currency.Trim();
        var amount = Math.Abs(mills);
        var text = (amount / (decimal)PerUnit).ToString(amount % 10 == 0 ? "0.00" : "0.000", CultureInfo.InvariantCulture);
        var sign = mills < 0 ? "-" : "";
        if (currency.Length == 0)
            return sign + text;
        return currency.All(char.IsLetter) ? $"{sign}{text} {currency}" : $"{sign}{currency}{text}";
    }

    /// <summary>Reads "1.25", "0.035", "-2", ".5" as mills. False for anything else, or finer than a thousandth.</summary>
    public static bool TryParse(string? text, out long mills)
    {
        mills = 0;
        if (string.IsNullOrWhiteSpace(text) ||
            !decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            return false;
        var scaled = amount * PerUnit;
        if (scaled != decimal.Truncate(scaled) || Math.Abs(scaled) > Max)
            return false;
        mills = (long)scaled;
        return true;
    }
}
