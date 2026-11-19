using GramWalletService.Payouts;

namespace GramWalletService.Persistence;

internal static class PayoutRecordMap
{
    public static PayoutEntity ToEntity(PayoutRecord record)
    {
        var entity = new PayoutEntity
        {
            Id = record.Id,
            Hashes = []
        };
        Copy(entity, record, replaceHashes: true);
        return entity;
    }

    public static PayoutRecord ToRecord(PayoutEntity entity)
    {
        var record = new PayoutRecord
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            DestinationRaw = entity.DestinationRaw,
            DestinationFriendly = entity.DestinationFriendly,
            AmountNano = entity.AmountNano,
            Comment = entity.Comment,
            Status = entity.Status,
            WalletSeqno = entity.WalletSeqno,
            ExternalMessageHash = entity.ExternalMessageHash,
            TransactionHash = entity.TransactionHash,
            SignedBoc = entity.SignedBoc,
            ValidUntilUtc = entity.ValidUntilUtc,
            Attempts = entity.Attempts,
            ResignCount = entity.ResignCount,
            SignFailures = entity.SignFailures,
            LastSend = entity.LastSend,
            LastBroadcastAtUtc = entity.LastBroadcastAtUtc,
            FeeReserveNano = entity.FeeReserveNano,
            LastError = entity.LastError,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            ConfirmedAtUtc = entity.ConfirmedAtUtc,
            InsufficientNotified = entity.InsufficientNotified,
            TerminalNotified = entity.TerminalNotified,
            SuccessNotified = entity.SuccessNotified
        };

        if (entity.Hashes is { Count: > 0 })
        {
            foreach (var hash in entity.Hashes.OrderBy(item => item.Ordinal))
                record.TrackedHashes.Add(hash.Hash);
        }

        return record;
    }

    public static void Copy(PayoutEntity entity, PayoutRecord record, bool replaceHashes)
    {
        entity.RequestId = record.RequestId;
        entity.DestinationRaw = record.DestinationRaw;
        entity.DestinationFriendly = record.DestinationFriendly;
        entity.AmountNano = record.AmountNano;
        entity.Comment = record.Comment;
        entity.Status = record.Status;
        entity.WalletSeqno = record.WalletSeqno;
        entity.ExternalMessageHash = record.ExternalMessageHash;
        entity.TransactionHash = record.TransactionHash;
        entity.SignedBoc = record.SignedBoc;
        entity.ValidUntilUtc = record.ValidUntilUtc;
        entity.Attempts = record.Attempts;
        entity.ResignCount = record.ResignCount;
        entity.SignFailures = record.SignFailures;
        entity.LastSend = record.LastSend;
        entity.LastBroadcastAtUtc = record.LastBroadcastAtUtc;
        entity.FeeReserveNano = record.FeeReserveNano;
        entity.LastError = record.LastError;
        entity.CreatedAtUtc = record.CreatedAtUtc;
        entity.UpdatedAtUtc = record.UpdatedAtUtc;
        entity.ConfirmedAtUtc = record.ConfirmedAtUtc;
        entity.InsufficientNotified = record.InsufficientNotified;
        entity.TerminalNotified = record.TerminalNotified;
        entity.SuccessNotified = record.SuccessNotified;
        SyncHashes(entity, record, replaceHashes);
    }

    private static void SyncHashes(PayoutEntity entity, PayoutRecord record, bool replaceHashes)
    {
        var ordered = entity.Hashes.OrderBy(hash => hash.Ordinal).ToList();
        if (ordered.Count == record.TrackedHashes.Count
            && !replaceHashes
            && PrefixEquals(ordered, record, ordered.Count))
        {
            return;
        }

        if (!replaceHashes
            && ordered.Count < record.TrackedHashes.Count
            && PrefixEquals(ordered, record, ordered.Count))
        {
            for (var i = ordered.Count; i < record.TrackedHashes.Count; i++)
            {
                entity.Hashes.Add(new PayoutTrackedHashEntity
                {
                    PayoutId = entity.Id,
                    Ordinal = i,
                    Hash = record.TrackedHashes[i]
                });
            }

            return;
        }

        entity.Hashes.Clear();
        for (var i = 0; i < record.TrackedHashes.Count; i++)
        {
            entity.Hashes.Add(new PayoutTrackedHashEntity
            {
                PayoutId = entity.Id,
                Ordinal = i,
                Hash = record.TrackedHashes[i]
            });
        }
    }

    private static bool PrefixEquals(List<PayoutTrackedHashEntity> ordered, PayoutRecord record, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(ordered[i].Hash, record.TrackedHashes[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
