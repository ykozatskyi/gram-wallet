using System.Security.Cryptography;
using GramWalletService.Ton.Models;

namespace GramWalletService.Ton;

internal readonly record struct ChainCheck(bool Ok, bool Permanent, string? Problem);

internal sealed record WalletChainSnapshot(
    string RawAddress,
    bool IsWallet,
    string? WalletType,
    IReadOnlyList<string> Interfaces,
    int? Seqno,
    long? WalletId,
    long? BalanceNano,
    bool? SignatureAllowed,
    string? Status,
    string? CodeHash);

internal static class WalletIdentity
{
    public static readonly byte[] V5R1CodeHash = Convert.FromHexString(
        "20834b7b72b112147e1b2fb457b84e74d1a30f04f737d4f62a668e9552d2b72f");

    public static ChainCheck Evaluate(WalletLocalIdentity local, WalletChainSnapshot? chain)
    {
        if (!local.CanSignLocal)
            return new ChainCheck(false, true, local.Problem ?? "Signing is not configured.");

        if (chain is null)
            return new ChainCheck(false, false, "Wallet state is not available yet.");

        if (!string.Equals(chain.RawAddress, local.DerivedAddress!.Raw, StringComparison.OrdinalIgnoreCase))
            return new ChainCheck(false, true, "On-chain wallet address does not match the configured wallet.");

        if (!chain.IsWallet)
            return new ChainCheck(false, true, "Account is not a wallet.");

        if (!IsV5R1(chain.WalletType, chain.Interfaces))
            return new ChainCheck(false, true, "Wallet version is not V5R1.");

        if (chain.CodeHash is null)
            return new ChainCheck(false, false, "Wallet code hash is unreadable.");

        if (!SameHash(chain.CodeHash, V5R1CodeHash))
            return new ChainCheck(false, true, "Wallet code does not match V5R1.");

        if (chain.WalletId is null)
            return new ChainCheck(false, false, "Wallet id is unreadable.");

        if (chain.WalletId != local.WalletId)
            return new ChainCheck(false, true, "On-chain wallet id does not match V5R1.");

        if (chain.SignatureAllowed is null)
            return new ChainCheck(false, false, "Wallet signature flag is unreadable.");

        if (chain.SignatureAllowed == false)
            return new ChainCheck(false, true, "Wallet is not accepting signatures.");

        if (string.IsNullOrWhiteSpace(chain.Status))
            return new ChainCheck(false, false, "Wallet status is unreadable.");

        if (!string.Equals(chain.Status, "active", StringComparison.OrdinalIgnoreCase))
            return new ChainCheck(false, true, "Wallet is not active.");

        if (chain.Seqno is null or < 0)
            return new ChainCheck(false, false, "Wallet seqno is unreadable.");

        return new ChainCheck(true, false, null);
    }

    public static WalletChainSnapshot? Read(WalletStatesResponse response, TonAddress wallet)
    {
        WalletStateModel? match = null;
        foreach (var candidate in response.Wallets ?? [])
        {
            if (SameAddress(candidate.Address, wallet))
            {
                match = candidate;
                break;
            }
        }

        if (match?.Address is null || !TonAddress.TryParse(match.Address, wallet.IsTestOnly, out var parsed))
            return null;

        var interfaces = new List<string>();
        if (response.AddressBook is not null)
        {
            foreach (var (key, entry) in response.AddressBook)
            {
                if (entry.Interfaces is null || !SameAddress(key, wallet))
                    continue;

                interfaces.AddRange(entry.Interfaces.Where(item => !string.IsNullOrWhiteSpace(item)));
            }
        }

        long? balance = null;
        if (GramAmount.TryNormalizeNano(match.Balance, out var nano)
            && long.TryParse(nano, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedBalance)
            && parsedBalance >= 0)
        {
            balance = parsedBalance;
        }

        return new WalletChainSnapshot(
            parsed.Raw,
            match.IsWallet ?? true,
            match.WalletType,
            interfaces,
            match.Seqno,
            match.WalletId,
            balance,
            match.IsSignatureAllowed,
            string.IsNullOrWhiteSpace(match.Status) ? null : match.Status.Trim().ToLowerInvariant(),
            match.CodeHash);
    }

    public static bool SameHash(string? actual, byte[] expected)
    {
        if (!TryDecodeHash(actual, out var bytes) || bytes.Length != expected.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(bytes, expected);
    }

    private static bool IsV5R1(string? walletType, IReadOnlyList<string> interfaces)
    {
        if (!string.IsNullOrWhiteSpace(walletType))
            return LooksLikeV5R1(walletType);

        return interfaces.Any(LooksLikeV5R1);
    }

    private static bool LooksLikeV5R1(string value)
    {
        var compact = value.Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return compact.Contains("v5r1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameAddress(string? value, TonAddress wallet)
    {
        return TonAddress.TryParse(value, wallet.IsTestOnly, out var parsed)
            && string.Equals(parsed.Raw, wallet.Raw, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDecodeHash(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        try
        {
            if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
            {
                bytes = Convert.FromHexString(trimmed);
                return true;
            }

            var standard = trimmed.Replace('-', '+').Replace('_', '/');
            var padding = standard.Length % 4;
            if (padding == 1)
                return false;

            if (padding > 0)
                standard = standard.PadRight(standard.Length + (4 - padding), '=');

            bytes = Convert.FromBase64String(standard);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
