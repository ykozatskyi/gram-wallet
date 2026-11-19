namespace GramWalletService.Ton;

internal static class TonHash
{
    public static bool Same(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
    }

    public static string? Normalize(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return null;

        var trimmed = hash.Trim();
        try
        {
            var bytes = trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit)
                ? Convert.FromHexString(trimmed)
                : DecodeBase64(trimmed);
            if (bytes.Length != 32)
                return null;

            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64(string text)
    {
        var standard = text.Replace('-', '+').Replace('_', '/');
        var padding = standard.Length % 4;
        if (padding == 1)
            throw new FormatException("Invalid hash.");

        if (padding > 0)
            standard = standard.PadRight(standard.Length + (4 - padding), '=');

        return Convert.FromBase64String(standard);
    }
}
