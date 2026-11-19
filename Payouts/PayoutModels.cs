using System.Globalization;
using System.Text.Json;
using GramWalletService.Configuration;
using GramWalletService.Models;
using GramWalletService.Ton;

namespace GramWalletService.Payouts;

internal static class PayoutLimits
{
    public const long NetworkFeeReserveNano = 50_000_000;
    public const int MaxCommentBytes = 123;
    public const int ValidForSeconds = 180;
    public const int MaxResigns = 3;
    public const int IndexGraceSeconds = 90;
    public const int RebroadcastDelaySeconds = 5;
    public const int ConfirmHoldSeconds = 45;
    public const int MaxListLimit = 50;
    public const int DefaultListLimit = 20;
    public const int TelegramPageSize = 10;
    public static readonly TimeSpan OutageNotificationAfter = TimeSpan.FromMinutes(3);
}

public enum PayoutStatus
{
    Queued,
    Signed,
    Broadcasted,
    Confirmed,
    Failed,
    NeedsReview
}

public enum SendDisposition
{
    None,
    Accepted,
    Rejected,
    Uncertain
}

internal sealed class PayoutRecord
{
    public Guid Id { get; set; }

    public string RequestId { get; set; } = "";

    public string DestinationRaw { get; set; } = "";

    public string DestinationFriendly { get; set; } = "";

    public long AmountNano { get; set; }

    public string? Comment { get; set; }

    public PayoutStatus Status { get; set; }

    public int? WalletSeqno { get; set; }

    public string? ExternalMessageHash { get; set; }

    public string? TransactionHash { get; set; }

    public string? SignedBoc { get; set; }

    public DateTimeOffset? ValidUntilUtc { get; set; }

    public int Attempts { get; set; }

    public int ResignCount { get; set; }

    public int SignFailures { get; set; }

    public SendDisposition LastSend { get; set; }

    public DateTimeOffset? LastBroadcastAtUtc { get; set; }

    public long FeeReserveNano { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? ConfirmedAtUtc { get; set; }

    public bool InsufficientNotified { get; set; }

    public bool TerminalNotified { get; set; }

    public bool SuccessNotified { get; set; }

    public List<string> TrackedHashes { get; } = [];

    public PayoutRecord Clone()
    {
        var copy = new PayoutRecord
        {
            Id = Id,
            RequestId = RequestId,
            DestinationRaw = DestinationRaw,
            DestinationFriendly = DestinationFriendly,
            AmountNano = AmountNano,
            Comment = Comment,
            Status = Status,
            WalletSeqno = WalletSeqno,
            ExternalMessageHash = ExternalMessageHash,
            TransactionHash = TransactionHash,
            SignedBoc = SignedBoc,
            ValidUntilUtc = ValidUntilUtc,
            Attempts = Attempts,
            ResignCount = ResignCount,
            SignFailures = SignFailures,
            LastSend = LastSend,
            LastBroadcastAtUtc = LastBroadcastAtUtc,
            FeeReserveNano = FeeReserveNano,
            LastError = LastError,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            ConfirmedAtUtc = ConfirmedAtUtc,
            InsufficientNotified = InsufficientNotified,
            TerminalNotified = TerminalNotified,
            SuccessNotified = SuccessNotified
        };
        copy.TrackedHashes.AddRange(TrackedHashes);
        return copy;
    }
}

internal sealed record PayoutCommand(string RequestId, string Address, string Amount, string? Comment);

internal sealed record NormalizedPayout(string RequestId, TonAddress Destination, long AmountNano, string? Comment);

internal abstract record PayoutResult
{
    internal sealed record Ok(PayoutRecord Payout, bool Created) : PayoutResult;

    internal sealed record Error(int Status, string Message) : PayoutResult;
}

internal sealed record PayoutSettingsSnapshot(
    bool Paused,
    bool SigningReady,
    bool SigningConfigured,
    string? SigningProblem,
    string MaxPayout,
    string MinReserve,
    int Queued,
    int NeedsReview,
    bool NotifyOnSuccess);

internal static class PayoutRequests
{
    public static bool TryRead(CreatePayoutRequest? request, out PayoutCommand command, out string error)
    {
        command = null!;
        if (request is null)
        {
            error = "Invalid request.";
            return false;
        }

        if (!TryReadAmount(request.Amount, out var amount))
        {
            error = "Amount must be a GRAM value.";
            return false;
        }

        command = new PayoutCommand(
            request.RequestId?.Trim() ?? "",
            request.Address?.Trim() ?? "",
            amount.Trim(),
            request.Comment);
        error = "";
        return true;
    }

    public static bool TryNormalize(PayoutCommand command, AppOptions options, out NormalizedPayout payout, out string error)
    {
        payout = null!;
        if (!Guid.TryParse(command.RequestId, out var requestId))
        {
            error = "requestId must be a UUID.";
            return false;
        }

        if (!TryDestination(command.Address, options, out var destination, out error))
            return false;

        if (!TryAmount(command.Amount, options, out var nano, out error))
            return false;

        if (!TryComment(command.Comment, out var comment, out error))
            return false;

        payout = new NormalizedPayout(requestId.ToString("D"), destination, nano, comment);
        error = "";
        return true;
    }

    public static bool TryDestination(string? address, AppOptions options, out TonAddress destination, out string error)
    {
        destination = null!;
        if (!TonAddress.TryParse(address, options.IsTestnet, out destination))
        {
            error = "Address is not a valid TON address.";
            return false;
        }

        if (destination.HasNetworkFlag && destination.IsTestOnly != options.IsTestnet)
        {
            error = "Address is on a different network.";
            return false;
        }

        if (string.Equals(destination.Raw, options.Wallet.Raw, StringComparison.OrdinalIgnoreCase))
        {
            error = "That address is our own wallet.";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryAmount(string? amount, AppOptions options, out long nano, out string error)
    {
        nano = 0;
        if (!GramAmount.TryParseGram(amount, out nano))
        {
            error = "Amount must be a GRAM value with up to 9 decimal places.";
            return false;
        }

        if (nano <= 0)
        {
            error = "Amount must be greater than zero.";
            return false;
        }

        if (nano > options.MaxPayoutNano)
        {
            error = "Amount is above the maximum payout.";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryComment(string? comment, out string? normalized, out string error)
    {
        normalized = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (normalized is not null)
        {
            if (normalized.Contains('\0'))
            {
                error = "Comment is not valid text.";
                return false;
            }

            if (System.Text.Encoding.UTF8.GetByteCount(normalized) > PayoutLimits.MaxCommentBytes)
            {
                error = "Comment must be 123 bytes or less.";
                return false;
            }
        }

        error = "";
        return true;
    }

    public static bool SameDetails(PayoutRecord existing, NormalizedPayout incoming)
    {
        return string.Equals(existing.DestinationRaw, incoming.Destination.Raw, StringComparison.OrdinalIgnoreCase)
            && existing.AmountNano == incoming.AmountNano
            && string.Equals(existing.Comment ?? "", incoming.Comment ?? "", StringComparison.Ordinal);
    }

    private static bool TryReadAmount(JsonElement element, out string amount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                amount = element.GetString() ?? "";
                return amount.Length > 0;
            case JsonValueKind.Number:
                amount = element.GetRawText();
                return amount.Length > 0;
            default:
                amount = "";
                return false;
        }
    }
}

internal static class PayoutMapper
{
    public static PayoutDto ToDto(PayoutRecord payout, AppOptions options)
    {
        return new PayoutDto(
            payout.Id,
            payout.RequestId,
            payout.DestinationFriendly,
            GramAmount.FormatGram(payout.AmountNano.ToString(CultureInfo.InvariantCulture)),
            payout.Comment,
            payout.Status.ToString(),
            payout.TransactionHash,
            payout.TransactionHash is null ? null : options.TransactionExplorerUrl(payout.TransactionHash),
            payout.Attempts,
            payout.LastError,
            payout.CreatedAtUtc,
            payout.UpdatedAtUtc,
            payout.ConfirmedAtUtc);
    }
}
