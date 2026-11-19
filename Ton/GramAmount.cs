using System.Globalization;

namespace GramWalletService.Ton;

internal static class GramAmount
{
    public const decimal NanoPerGram = 1_000_000_000m;

    static GramAmount()
    {
        if (FormatGram("0") != "0"
            || FormatGram("1") != "0.000000001"
            || FormatGram("1000000000") != "1"
            || FormatGram("1500000000") != "1.5"
            || FormatGram("1592546925409142") != "1592546.925409142")
        {
            throw new InvalidOperationException("GRAM amount formatting failed its self-check.");
        }
    }

    public static bool TryNormalizeNano(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 40)
            return false;

        if (!decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return false;

        normalized = value.ToString("0", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryParseGram(string? text, out long nano)
    {
        nano = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 40)
            return false;

        if (trimmed[0] is '+' or '-' || trimmed.Contains('_') || trimmed.Contains(',')
            || trimmed.Contains('e') || trimmed.Contains('E') || trimmed.Contains(' '))
        {
            return false;
        }

        var dot = trimmed.IndexOf('.');
        if (dot != trimmed.LastIndexOf('.'))
            return false;

        if (dot == 0 || dot == trimmed.Length - 1)
            return false;

        var whole = dot < 0 ? trimmed : trimmed[..dot];
        var fraction = dot < 0 ? string.Empty : trimmed[(dot + 1)..];
        if (whole.Length == 0 || fraction.Length > 9)
            return false;

        if (!whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit))
            return false;

        if (whole.Length > 1 && whole[0] == '0')
            return false;

        if (!ulong.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var wholeUnits))
            return false;

        var fractionNano = 0u;
        if (fraction.Length > 0
            && !uint.TryParse(fraction.PadRight(9, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out fractionNano))
        {
            return false;
        }

        if (wholeUnits > (ulong)long.MaxValue / 1_000_000_000UL)
            return false;

        var total = (wholeUnits * 1_000_000_000UL) + fractionNano;
        if (total > long.MaxValue)
            return false;

        nano = (long)total;
        return true;
    }

    public static string FormatGram(string nanoAmount)
    {
        if (!TryNormalizeNano(nanoAmount, out var normalized))
            throw new FormatException("Nano amount is not an integer.");

        var nano = decimal.Parse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.Integer, CultureInfo.InvariantCulture);
        var gram = decimal.Divide(nano, NanoPerGram);
        return gram.ToString("0.#########", CultureInfo.InvariantCulture);
    }
}
