using GramWalletService.Payouts;

namespace GramWalletService.Persistence;

public sealed class PayoutEntity
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

    public List<PayoutTrackedHashEntity> Hashes { get; set; } = [];
}
