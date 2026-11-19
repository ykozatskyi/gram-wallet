using System.Globalization;
using GramWalletService.Ton;
using Npgsql;

namespace GramWalletService.Configuration;

public sealed class AppOptions
{
    private AppOptions(
        string? telegramBotToken,
        long telegramAdminId,
        string network,
        string? tonApiKey,
        string apiKey,
        TonAddress wallet,
        string? mnemonicPhrase,
        string? mnemonicProblem,
        bool payoutsEnabled,
        long maxPayoutNano,
        long minReserveNano,
        string databaseConnectionString,
        string? databasePassword)
    {
        TelegramBotToken = telegramBotToken;
        TelegramAdminId = telegramAdminId;
        Network = network;
        TonApiKey = tonApiKey;
        ApiKey = apiKey;
        Wallet = wallet;
        MnemonicPhrase = mnemonicPhrase;
        MnemonicProblem = mnemonicProblem;
        PayoutsEnabled = payoutsEnabled;
        MaxPayoutNano = maxPayoutNano;
        MinReserveNano = minReserveNano;
        DatabaseConnectionString = databaseConnectionString;
        DatabasePassword = databasePassword;
    }

    public string? TelegramBotToken { get; }

    public long TelegramAdminId { get; }

    public string Network { get; }

    public string? TonApiKey { get; }

    public string ApiKey { get; }

    internal TonAddress Wallet { get; }

    internal string? MnemonicPhrase { get; }

    internal string? MnemonicProblem { get; }

    public bool PayoutsEnabled { get; }

    public long MaxPayoutNano { get; }

    public long MinReserveNano { get; }

    internal string DatabaseConnectionString { get; }

    internal string? DatabasePassword { get; }

    public bool IsTestnet => Network == "testnet";

    public string NetworkLabel => IsTestnet ? "TON Testnet" : "TON Mainnet";

    public string TonBaseUrl => IsTestnet
        ? "https://testnet.toncenter.com/api/v3/"
        : "https://toncenter.com/api/v3/";

    public string ExplorerHost => IsTestnet ? "https://testnet.tonviewer.com" : "https://tonviewer.com";

    public string AddressExplorerUrl => ExplorerHost + "/" + Wallet.NonBounceable;

    public string TransactionExplorerUrl(string hash)
    {
        return ExplorerHost + "/transaction/" + Uri.EscapeDataString(TonAddress.ToExplorerHash(hash));
    }

    public static AppOptions Create(IConfiguration configuration)
    {
        var errors = new List<string>();

        var tokenText = configuration["TELEGRAM_BOT_TOKEN"]?.Trim();
        string? token = null;
        if (!string.IsNullOrEmpty(tokenText))
        {
            if (!IsBotToken(tokenText))
                errors.Add("TELEGRAM_BOT_TOKEN is not a bot token.");
            else
                token = tokenText;
        }

        var adminText = configuration["TELEGRAM_ADMIN_ID"]?.Trim();
        var adminId = 0L;
        if (!long.TryParse(adminText, NumberStyles.Integer, CultureInfo.InvariantCulture, out adminId) || adminId <= 0)
            errors.Add("TELEGRAM_ADMIN_ID must be a positive Telegram user id.");

        var network = configuration["TON_NETWORK"]?.Trim().ToLowerInvariant();
        if (network is not ("mainnet" or "testnet"))
            errors.Add("TON_NETWORK must be mainnet or testnet.");

        var tonApiKey = configuration["TON_API_KEY"]?.Trim();
        if (string.IsNullOrEmpty(tonApiKey))
        {
            tonApiKey = null;
        }
        else if (tonApiKey.Any(char.IsWhiteSpace) || tonApiKey.Length < 8)
        {
            errors.Add("TON_API_KEY is set but does not look usable.");
            tonApiKey = null;
        }

        var apiKey = configuration["API_KEY"]?.Trim();
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length is < 16 or > 256 || apiKey.Any(char.IsWhiteSpace))
            errors.Add("API_KEY must be 16 to 256 characters and is sent as X-Api-Key.");

        TonAddress? wallet = null;
        var walletText = configuration["TON_WALLET_ADDRESS"]?.Trim();
        if (string.IsNullOrEmpty(walletText))
        {
            errors.Add("TON_WALLET_ADDRESS is missing.");
        }
        else if (!TonAddress.TryParse(walletText, network == "testnet", out var parsed))
        {
            errors.Add("TON_WALLET_ADDRESS is not a valid TON address.");
        }
        else if (parsed.HasNetworkFlag && network is "mainnet" or "testnet" && parsed.IsTestOnly != (network == "testnet"))
        {
            errors.Add("TON_WALLET_ADDRESS does not match TON_NETWORK.");
        }
        else
        {
            wallet = parsed;
        }

        var (mnemonic, mnemonicProblem) = ReadMnemonic(configuration);
        if (!TryReadBool(configuration["GRAM_PAYOUT_ENABLED"], defaultValue: false, out var payoutsEnabled))
            errors.Add("GRAM_PAYOUT_ENABLED must be true or false.");

        if (!TryReadGram(configuration["GRAM_MAX_PAYOUT"], defaultNano: 10_000_000_000L, out var maxPayoutNano) || maxPayoutNano <= 0)
            errors.Add("GRAM_MAX_PAYOUT must be a positive GRAM amount.");

        if (!TryReadGram(configuration["GRAM_MIN_RESERVE"], defaultNano: 100_000_000L, out var minReserveNano) || minReserveNano < 0)
            errors.Add("GRAM_MIN_RESERVE must be zero or a positive GRAM amount.");

        var (databaseConnectionString, databasePassword) = ReadDatabase(configuration, errors);

        if (errors.Count > 0 || apiKey is null || wallet is null || network is not ("mainnet" or "testnet") || databaseConnectionString is null)
            throw new InvalidOperationException("Configuration is invalid:\n- " + string.Join("\n- ", errors));

        return new AppOptions(
            token,
            adminId,
            network,
            tonApiKey,
            apiKey,
            wallet,
            mnemonic,
            mnemonicProblem,
            payoutsEnabled,
            maxPayoutNano,
            minReserveNano,
            databaseConnectionString,
            databasePassword);
    }

    public override string ToString()
    {
        return "AppOptions(" + Network + ", " + Wallet.NonBounceable + ")";
    }

    private static (string? ConnectionString, string? Password) ReadDatabase(IConfiguration configuration, List<string> errors)
    {
        var text = configuration["DATABASE_CONNECTION_STRING"]?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            errors.Add("DATABASE_CONNECTION_STRING is missing.");
            return (null, null);
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(text);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
            {
                errors.Add("DATABASE_CONNECTION_STRING must include Host and Database.");
                return (null, null);
            }

            if (!string.Equals(builder.Database, "GRAMWalletDB", StringComparison.Ordinal))
            {
                errors.Add("DATABASE_CONNECTION_STRING must target database GRAMWalletDB.");
                return (null, null);
            }

            var password = string.IsNullOrEmpty(builder.Password) ? null : builder.Password;
            return (text, password);
        }
        catch (ArgumentException)
        {
            errors.Add("DATABASE_CONNECTION_STRING is not a valid PostgreSQL connection string.");
            return (null, null);
        }
    }

    private static (string? Phrase, string? Problem) ReadMnemonic(IConfiguration configuration)
    {
        var raw = configuration["TON_MNEMONIC"]?.Trim();
        if (string.IsNullOrEmpty(raw))
            return (null, null);

        if (raw.Length > 500)
            return (null, "TON_MNEMONIC is not a 24-word phrase.");

        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 24
            || words.Any(word => word.Length is < 2 or > 12 || word.Any(ch => !char.IsLetter(ch))))
        {
            return (null, "TON_MNEMONIC is not a 24-word phrase.");
        }

        return (string.Join(' ', words.Select(word => word.ToLowerInvariant())), null);
    }

    private static bool TryReadBool(string? text, bool defaultValue, out bool value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = defaultValue;
            return true;
        }

        if (bool.TryParse(text.Trim(), out value))
            return true;

        value = defaultValue;
        return false;
    }

    private static bool TryReadGram(string? text, long defaultNano, out long nano)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            nano = defaultNano;
            return true;
        }

        return GramAmount.TryParseGram(text, out nano);
    }

    private static bool IsBotToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            return false;

        var index = token.IndexOf(':');
        return index is >= 1 and <= 16
            && long.TryParse(token[..index], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
