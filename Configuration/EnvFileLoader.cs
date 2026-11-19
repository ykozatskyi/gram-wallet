namespace GramWalletService.Configuration;

internal static class EnvFileLoader
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "TELEGRAM_BOT_TOKEN",
        "TELEGRAM_ADMIN_ID",
        "TON_NETWORK",
        "TON_API_KEY",
        "TON_WALLET_ADDRESS",
        "API_KEY",
        "TON_MNEMONIC",
        "GRAM_PAYOUT_ENABLED",
        "GRAM_MAX_PAYOUT",
        "GRAM_MIN_RESERVE",
        "DATABASE_CONNECTION_STRING"
    };

    public static void Load()
    {
        var path = FindEnvFile();
        if (path is null)
            return;

        var text = File.ReadAllText(path);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (key.Length == 0)
                continue;

            if (IsWalletSecret(key) && !string.Equals(key, "TON_MNEMONIC", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Ignoring .env entry '{key}'. That wallet secret is not used.");
                continue;
            }

            if (!AllowedKeys.Contains(key) || string.IsNullOrEmpty(value))
                continue;

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                continue;

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static bool IsWalletSecret(string key)
    {
        return key.Contains("MNEMONIC", StringComparison.OrdinalIgnoreCase)
            || key.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase)
            || key.Contains("SEED", StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var quote = value[0];
            if ((quote == '"' || quote == '\'') && value[^1] == quote)
                return value[1..^1];
        }

        return value;
    }

    private static string? FindEnvFile()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".env");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
