using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace WorkFor.Core;

public static partial class CurrencyCatalog
{
    private static readonly Dictionary<string, int> Digits = new(StringComparer.Ordinal)
    { ["CNY"] = 2, ["USD"] = 2, ["EUR"] = 2, ["GBP"] = 2, ["JPY"] = 0, ["KWD"] = 3, ["HKD"] = 2, ["TWD"] = 2 };
    public static CurrencyInfo Get(string code) => Digits.TryGetValue(code, out var digits) ? new(code, digits) : throw new ArgumentException("Unsupported currency.");
    public static void Validate(CurrencyInfo currency)
    {
        if (!Digits.TryGetValue(currency.Code, out var digits) || digits != currency.FractionDigits) throw new ArgumentException("Unsupported currency or fraction digits.");
    }
}
public static partial class MoneyFormatter
{
    [GeneratedRegex(@"^(0|-[1-9][0-9]{0,23}|[1-9][0-9]{0,23})$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();
    public static bool IsCanonical(string value) => value is not null && CanonicalPattern().IsMatch(value);
    public static string Format(string minor, CurrencyInfo currency, string locale = "zh-CN")
    {
        CurrencyCatalog.Validate(currency);
        if (!IsCanonical(minor)) throw new ArgumentException("Invalid minor units.");
        var amount = BigInteger.Parse(minor, CultureInfo.InvariantCulture);
        var negative = amount.Sign < 0;
        var digits = BigInteger.Abs(amount).ToString(CultureInfo.InvariantCulture).PadLeft(currency.FractionDigits + 1, '0');
        var integer = currency.FractionDigits == 0 ? digits : digits[..^currency.FractionDigits];
        var culture = CultureInfo.GetCultureInfo(locale);
        var number = BigInteger.Parse(integer, CultureInfo.InvariantCulture).ToString("N0", culture);
        if (currency.FractionDigits > 0) number += culture.NumberFormat.NumberDecimalSeparator + digits[^currency.FractionDigits..];
        var symbol = currency.Code switch { "CNY" => "¥", "USD" => "$", "EUR" => "€", "GBP" => "£", "JPY" => "JP¥", "HKD" => "HK$", "TWD" => "NT$", _ => currency.Code + " " };
        return (negative ? "-" : "") + symbol + number;
    }
}
