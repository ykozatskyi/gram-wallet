using System.Text.RegularExpressions;

namespace GramWalletService.Configuration;

internal static partial class SecretRedactor
{
    public static string Redact(string? text, AppOptions options)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var redacted = text;
        redacted = Replace(redacted, options.TelegramBotToken);
        redacted = Replace(redacted, options.TonApiKey);
        redacted = Replace(redacted, options.ApiKey);
        redacted = Replace(redacted, options.MnemonicPhrase);
        redacted = Replace(redacted, options.DatabasePassword);
        redacted = BocPattern().Replace(redacted, "***");
        redacted = LongHexPattern().Replace(redacted, "***");
        return redacted;
    }

    [GeneratedRegex(@"te6[A-Za-z0-9+/=_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex BocPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{100,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHexPattern();

    private static string Replace(string text, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return text;

        return text.Replace(secret, "***", StringComparison.Ordinal);
    }
}
