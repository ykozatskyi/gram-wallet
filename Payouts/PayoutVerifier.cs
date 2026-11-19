using System.Globalization;
using GramWalletService.Ton;
using GramWalletService.Ton.Models;

namespace GramWalletService.Payouts;

internal enum PayoutVerdictKind
{
    Pending,
    Confirmed,
    Failed,
    NeedsReview,
    Resign,
    Rebroadcast
}

internal readonly record struct PayoutVerdict(PayoutVerdictKind Kind, string? TransactionHash, string? Reason)
{
    public static PayoutVerdict Pending() => new(PayoutVerdictKind.Pending, null, null);

    public static PayoutVerdict Confirmed(string? hash) => new(PayoutVerdictKind.Confirmed, hash, null);

    public static PayoutVerdict Failed(string? hash, string reason) => new(PayoutVerdictKind.Failed, hash, reason);

    public static PayoutVerdict NeedsReview(string? hash, string reason) => new(PayoutVerdictKind.NeedsReview, hash, reason);

    public static PayoutVerdict Resign() => new(PayoutVerdictKind.Resign, null, null);

    public static PayoutVerdict Rebroadcast() => new(PayoutVerdictKind.Rebroadcast, null, null);
}

internal readonly record struct PayoutObservation(
    string WalletRaw,
    bool Testnet,
    string DestinationRaw,
    long AmountNano,
    string? Comment,
    int SignedSeqno,
    DateTimeOffset ValidUntilUtc,
    int ResignCount,
    SendDisposition LastSend,
    IReadOnlyList<string> Hashes,
    DateTimeOffset Now);

internal static class PayoutVerifier
{
    public static PayoutVerdict Review(
        PayoutObservation observation,
        int currentSeqno,
        IReadOnlyList<TransactionModel> transactions,
        IReadOnlyList<ActionModel> actions)
    {
        var walletTransaction = FindWalletTransaction(observation, transactions);
        if (walletTransaction is null)
            return Missing(observation, currentSeqno);

        if (walletTransaction.Emulated == true)
            return PayoutVerdict.Pending();

        var outgoing = FindOutgoing(walletTransaction, observation);
        var hash = walletTransaction.Hash;
        if (walletTransaction.Description?.Aborted == true
            || walletTransaction.Description?.ComputePh is { Skipped: not true, Success: false })
        {
            return outgoing is null
                ? PayoutVerdict.Failed(hash, "Wallet transaction aborted before the transfer.")
                : PayoutVerdict.NeedsReview(hash, "Wallet transaction aborted after creating a transfer.");
        }

        if (walletTransaction.Description?.Action?.NoFunds == true)
            return PayoutVerdict.Failed(hash, "Wallet didn't have enough GRAM to send.");

        if (walletTransaction.Description?.Action?.Success == false)
        {
            return outgoing is null
                ? PayoutVerdict.Failed(hash, "Wallet action failed.")
                : PayoutVerdict.NeedsReview(hash, "Wallet action failed after creating a transfer.");
        }

        if (outgoing is null)
            return PayoutVerdict.NeedsReview(hash, "The wallet transaction didn't include the expected transfer.");

        if (outgoing.Count > 1)
            return PayoutVerdict.NeedsReview(hash, "The wallet transaction included more than the expected transfer.");

        if (outgoing[0].Bounced == true)
            return PayoutVerdict.Failed(hash, "The transfer bounced.");

        var action = FindAction(actions, observation);
        if (action is null)
            return PayoutVerdict.Pending();

        if (action.Details?.Encrypted == true)
            return PayoutVerdict.NeedsReview(hash, "The transfer was encrypted, so it was not the payout we signed.");

        if (!Settled(action))
            return PayoutVerdict.Pending();

        if (observation.Comment is not null
            && action.Details?.Comment is not null
            && !string.Equals(action.Details.Comment, observation.Comment, StringComparison.Ordinal))
        {
            return PayoutVerdict.NeedsReview(hash, "Transfer comment does not match.");
        }

        return action.Success == true
            ? PayoutVerdict.Confirmed(hash)
            : PayoutVerdict.Failed(hash, "The transfer failed on-chain.");
    }

    public static void SelfCheck()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var uncertain = new PayoutObservation("0:aa", false, "0:bb", 1, null, 7, now.AddMinutes(1), 0, SendDisposition.Uncertain, [], now);
        var live = Review(uncertain, 7, [], []);
        var moved = Review(uncertain, 8, [], []);
        var grace = Review(uncertain with { ValidUntilUtc = now.AddSeconds(-10) }, 7, [], []);
        var stuck = Review(
            uncertain with { ValidUntilUtc = now.AddSeconds(-(PayoutLimits.IndexGraceSeconds + 5)) },
            7,
            [],
            []);
        var rejected = Review(uncertain with { LastSend = SendDisposition.Rejected }, 7, [], []);
        if (live.Kind != PayoutVerdictKind.Rebroadcast
            || moved.Kind != PayoutVerdictKind.Pending
            || grace.Kind != PayoutVerdictKind.Pending
            || stuck.Kind != PayoutVerdictKind.NeedsReview
            || rejected.Kind != PayoutVerdictKind.Resign)
        {
            throw new InvalidOperationException("Payout recovery self-check failed.");
        }
    }

    private static PayoutVerdict Missing(PayoutObservation observation, int currentSeqno)
    {
        var expired = observation.Now >= observation.ValidUntilUtc;
        var seqnoMoved = currentSeqno != observation.SignedSeqno;
        if (observation.LastSend == SendDisposition.Uncertain)
        {
            // The same signed message can be sent again. A new signature waits for a human.
            if (!expired && !seqnoMoved)
                return PayoutVerdict.Rebroadcast();

            if (!expired || observation.Now < observation.ValidUntilUtc.AddSeconds(PayoutLimits.IndexGraceSeconds))
                return PayoutVerdict.Pending();

            return PayoutVerdict.NeedsReview(null, "Broadcast was uncertain and the payout was not found on-chain.");
        }

        if (!seqnoMoved && observation.LastSend == SendDisposition.Rejected)
            return observation.ResignCount >= PayoutLimits.MaxResigns
                ? PayoutVerdict.NeedsReview(null, "The signed message was rejected and was not accepted.")
                : PayoutVerdict.Resign();

        if (!expired && !seqnoMoved)
            return PayoutVerdict.Rebroadcast();

        if (!expired)
            return PayoutVerdict.Pending();

        if (!seqnoMoved)
        {
            return observation.ResignCount >= PayoutLimits.MaxResigns
                ? PayoutVerdict.NeedsReview(null, "The signed message expired and was not accepted.")
                : PayoutVerdict.Resign();
        }

        if (observation.Now < observation.ValidUntilUtc.AddSeconds(PayoutLimits.IndexGraceSeconds))
            return PayoutVerdict.Pending();

        return PayoutVerdict.NeedsReview(null, "Seqno moved but the payout transaction was not found.");
    }

    private static TransactionModel? FindWalletTransaction(PayoutObservation observation, IReadOnlyList<TransactionModel> transactions)
    {
        TransactionModel? fallback = null;
        foreach (var transaction in transactions)
        {
            if (transaction.Emulated == true)
                continue;

            if (!string.IsNullOrWhiteSpace(transaction.Account)
                && !SameAccount(transaction.Account, observation.WalletRaw, observation.Testnet))
            {
                continue;
            }

            if (transaction.InMsg is null || !string.IsNullOrWhiteSpace(transaction.InMsg.Source))
                continue;

            if (transaction.InMsg.Hash is null || !observation.Hashes.Any(hash => TonHash.Same(hash, transaction.InMsg.Hash)))
                continue;

            if (FindOutgoing(transaction, observation) is { Count: > 0 })
                return transaction;

            fallback ??= transaction;
        }

        return fallback;
    }

    private static List<MessageModel>? FindOutgoing(TransactionModel transaction, PayoutObservation observation)
    {
        var matches = new List<MessageModel>();
        foreach (var message in transaction.OutMsgs ?? [])
        {
            if (!SameAccount(message.Destination, observation.DestinationRaw, observation.Testnet))
                continue;

            if (!TryNano(message.Value, out var nano) || nano != observation.AmountNano)
                continue;

            matches.Add(message);
        }

        return matches.Count == 0 ? null : matches;
    }

    private static ActionModel? FindAction(IReadOnlyList<ActionModel> actions, PayoutObservation observation)
    {
        foreach (var action in actions)
        {
            if (!string.Equals(action.Type, "ton_transfer", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!SameAccount(action.Details?.Source, observation.WalletRaw, observation.Testnet)
                || !SameAccount(action.Details?.Destination, observation.DestinationRaw, observation.Testnet))
            {
                continue;
            }

            if (!TryNano(action.Details?.Value, out var nano) || nano != observation.AmountNano)
                continue;

            return action;
        }

        return null;
    }

    private static bool Settled(ActionModel action)
    {
        return action.Finality is null
            || action.Finality.Equals("finalized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameAccount(string? address, string raw, bool testnet)
    {
        return TonAddress.TryParse(address, testnet, out var parsed)
            && string.Equals(parsed.Raw, raw, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNano(string? value, out long nano)
    {
        nano = 0;
        return GramAmount.TryNormalizeNano(value, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out nano)
            && nano >= 0;
    }
}
