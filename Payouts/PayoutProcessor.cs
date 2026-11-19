using System.Globalization;
using GramWalletService.Configuration;
using GramWalletService.Ton;
using GramWalletService.Ton.Models;

namespace GramWalletService.Payouts;

internal sealed class PayoutProcessor
{
    private readonly PayoutStore _store;
    private readonly TonCenterClient _client;
    private readonly IWalletSigner _signer;
    private readonly WalletSigningGate _gate;
    private readonly IPayoutNotifier _notifier;
    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayoutProcessor> _logger;
    private readonly TimeSpan _outageAfter;
    private readonly SemaphoreSlim _run = new(1, 1);
    private DateTimeOffset? _outageSince;
    private bool _outageNotified;
    private bool _loggedBlock;
    private int _resignsThisRun;

    public PayoutProcessor(
        PayoutStore store,
        TonCenterClient client,
        IWalletSigner signer,
        WalletSigningGate gate,
        IPayoutNotifier notifier,
        AppOptions options,
        TimeProvider time,
        ILogger<PayoutProcessor> logger,
        TimeSpan? outageAfter = null)
    {
        _store = store;
        _client = client;
        _signer = signer;
        _gate = gate;
        _notifier = notifier;
        _options = options;
        _time = time;
        _logger = logger;
        _outageAfter = outageAfter ?? PayoutLimits.OutageNotificationAfter;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _resignsThisRun = 0;
            await RefreshChainAsync(cancellationToken).ConfigureAwait(false);
            foreach (var payout in await _store.InFlightAsync(cancellationToken).ConfigureAwait(false))
                await ReconcileAsync(payout, cancellationToken).ConfigureAwait(false);

            if (await _store.HasInFlightAsync(cancellationToken).ConfigureAwait(false)
                || await _store.IsPausedAsync(cancellationToken).ConfigureAwait(false)
                || !_gate.CanSign)
            {
                return;
            }

            var next = await _store.NextQueuedAsync(cancellationToken).ConfigureAwait(false);
            if (next is not null)
                await SignAndBroadcastAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (TonCenterException ex) when (IsTransport(ex))
        {
            _logger.LogWarning("Payout chain call failed ({Kind}).", ex.Kind);
            await NoteOutageAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.Release();
        }
    }

    private async Task RefreshChainAsync(CancellationToken cancellationToken)
    {
        if (_gate.PermanentlyBlocked || _gate.CanSign)
            return;

        var snapshot = await ReadWalletAsync(cancellationToken).ConfigureAwait(false);
        ApplyChain(WalletIdentity.Evaluate(_signer.Identity, snapshot));
    }

    private async Task SignAndBroadcastAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        // Hold the wallet lock across the chain read and the sign so a second payout cannot be signed.
        await using var lease = await _store.AcquireWalletLeaseAsync(cancellationToken).ConfigureAwait(false);
        var current = await _store.ReadAsync(lease, payout.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status != PayoutStatus.Queued)
            return;

        if (await _store.HasInFlightAsync(lease, cancellationToken).ConfigureAwait(false)
            || await _store.IsPausedAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        payout = current;
        var snapshot = await ReadWalletAsync(cancellationToken).ConfigureAwait(false);
        var check = WalletIdentity.Evaluate(_signer.Identity, snapshot);
        ApplyChain(check);
        if (!check.Ok || snapshot?.Seqno is null || snapshot.BalanceNano is null)
        {
            await _store.UpdateAsync(payout.Id, record => record.LastError = check.Problem ?? "Wallet state is incomplete.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var destination = await ReadDestinationAsync(payout, cancellationToken).ConfigureAwait(false);
        if (destination is null)
            return;

        if (destination == "frozen")
        {
            await FinishAsync(payout.Id, PayoutStatus.Failed, "Destination account is frozen.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (destination is not ("active" or "uninit" or "nonexist"))
        {
            await _store.UpdateAsync(payout.Id, record => record.LastError = "Destination account state is unknown.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var reserved = await _store.ReservedNanoAsync(lease, payout.Id, cancellationToken).ConfigureAwait(false);
        var need = payout.AmountNano + payout.FeeReserveNano;
        if (snapshot.BalanceNano.Value < _options.MinReserveNano
            || reserved > snapshot.BalanceNano.Value - _options.MinReserveNano
            || snapshot.BalanceNano.Value - _options.MinReserveNano - reserved < need)
        {
            await NoteInsufficientAsync(payout, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await _store.IsPausedAsync(lease, cancellationToken).ConfigureAwait(false))
            return;

        SignedTransfer signed;
        try
        {
            signed = _signer.Sign(new NativeTransfer(
                TonAddress.Parse(payout.DestinationRaw, _options.IsTestnet),
                destination == "active",
                payout.AmountNano,
                payout.Comment,
                snapshot.Seqno.Value,
                _time.GetUtcNow().AddSeconds(PayoutLimits.ValidForSeconds)));
        }
        catch (InvalidOperationException)
        {
            await NoteSignFailureAsync(payout.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var applied = false;
        await _store.UpdateAsync(lease, payout.Id, record =>
        {
            if (record.Status != PayoutStatus.Queued)
                return;

            applied = true;
            var from = record.Status;
            record.Status = PayoutStatus.Signed;
            record.WalletSeqno = signed.Seqno;
            record.ValidUntilUtc = signed.ValidUntilUtc;
            record.SignedBoc = signed.BocBase64;
            record.ExternalMessageHash = signed.MessageHashHex;
            Track(record, signed.MessageHashHex, signed.MessageHashBase64);
            record.LastBroadcastAtUtc = _time.GetUtcNow();
            record.LastSend = SendDisposition.None;
            record.Attempts++;
            record.LastError = null;
            record.InsufficientNotified = false;
            _logger.LogInformation("Payout {PayoutId} {FromStatus} -> Signed.", record.Id, from);
        }, cancellationToken).ConfigureAwait(false);
        if (!applied)
            return;

        // Durable before send. A restart reconciles this row instead of signing a replacement.
        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        await BroadcastAsync(payout.Id, signed.BocBase64, cancellationToken).ConfigureAwait(false);
        var saved = await _store.FindAsync(payout.Id, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
            await ReconcileAsync(saved, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        var current = await _store.FindAsync(payout.Id, cancellationToken).ConfigureAwait(false) ?? payout;
        if (current.Status is not (PayoutStatus.Signed or PayoutStatus.Broadcasted))
            return;

        if (current.WalletSeqno is null || current.ValidUntilUtc is null || string.IsNullOrWhiteSpace(current.SignedBoc))
        {
            await FinishAsync(current.Id, PayoutStatus.NeedsReview, "Signed payout is missing its message.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var snapshot = await ReadWalletAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot?.Seqno is null)
            return;

        var transactions = new List<TransactionModel>();
        foreach (var hash in current.TrackedHashes)
        {
            var page = await _client.GetTransactionsByMessageAsync(hash, cancellationToken).ConfigureAwait(false);
            ChainReached();
            if (page.Transactions is not null)
                transactions.AddRange(page.Transactions);
        }

        var actions = new List<ActionModel>();
        foreach (var transaction in transactions)
        {
            if (string.IsNullOrWhiteSpace(transaction.Hash))
                continue;

            var page = await _client.GetActionsByTransactionAsync(transaction.Hash, cancellationToken).ConfigureAwait(false);
            ChainReached();
            if (page.Actions is not null)
                actions.AddRange(page.Actions);
        }

        var verdict = PayoutVerifier.Review(Observe(current), snapshot.Seqno.Value, transactions, actions);
        switch (verdict.Kind)
        {
            case PayoutVerdictKind.Pending:
                return;
            case PayoutVerdictKind.Confirmed:
                await FinishAsync(current.Id, PayoutStatus.Confirmed, null, verdict.TransactionHash, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.Failed:
                await FinishAsync(current.Id, PayoutStatus.Failed, verdict.Reason ?? "The transfer failed.", verdict.TransactionHash, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.NeedsReview:
                await FinishAsync(current.Id, PayoutStatus.NeedsReview, verdict.Reason ?? "The transfer needs a look.", verdict.TransactionHash, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.Rebroadcast:
                if (await _store.IsPausedAsync(cancellationToken).ConfigureAwait(false))
                    return;

                if (current.LastBroadcastAtUtc is { } sentAt
                    && _time.GetUtcNow() - sentAt < TimeSpan.FromSeconds(PayoutLimits.RebroadcastDelaySeconds))
                {
                    return;
                }

                await _store.UpdateAsync(current.Id, record =>
                {
                    record.LastBroadcastAtUtc = _time.GetUtcNow();
                    record.Attempts++;
                }, cancellationToken).ConfigureAwait(false);
                await BroadcastAsync(current.Id, current.SignedBoc, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.Resign:
                if (await _store.IsPausedAsync(cancellationToken).ConfigureAwait(false) || _resignsThisRun >= 1)
                    return;

                _resignsThisRun++;
                await ResignAsync(current, snapshot.Seqno.Value, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task ResignAsync(PayoutRecord payout, int seqno, CancellationToken cancellationToken)
    {
        if (seqno != payout.WalletSeqno)
        {
            await FinishAsync(payout.Id, PayoutStatus.NeedsReview, "Seqno moved before the replacement message was signed.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var current = await _store.FindAsync(payout.Id, cancellationToken).ConfigureAwait(false) ?? payout;
        if (current.ResignCount >= PayoutLimits.MaxResigns)
        {
            await FinishAsync(current.Id, PayoutStatus.NeedsReview, "The signed message expired and was not accepted.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var destination = await ReadDestinationAsync(current, cancellationToken).ConfigureAwait(false);
        if (destination == "frozen")
        {
            await FinishAsync(current.Id, PayoutStatus.Failed, "Destination account is frozen.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (destination is not ("active" or "uninit" or "nonexist"))
            return;

        await using var lease = await _store.AcquireWalletLeaseAsync(cancellationToken).ConfigureAwait(false);
        var locked = await _store.ReadAsync(lease, current.Id, cancellationToken).ConfigureAwait(false);
        if (locked is null
            || locked.Status is not (PayoutStatus.Signed or PayoutStatus.Broadcasted)
            || locked.WalletSeqno != seqno
            || locked.ResignCount != current.ResignCount
            || !string.Equals(locked.SignedBoc, current.SignedBoc, StringComparison.Ordinal)
            || await _store.IsPausedAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SignedTransfer signed;
        try
        {
            signed = _signer.Sign(new NativeTransfer(
                TonAddress.Parse(locked.DestinationRaw, _options.IsTestnet),
                destination == "active",
                locked.AmountNano,
                locked.Comment,
                seqno,
                _time.GetUtcNow().AddSeconds(PayoutLimits.ValidForSeconds)));
        }
        catch (InvalidOperationException)
        {
            await NoteSignFailureAsync(locked.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var applied = false;
        await _store.UpdateAsync(lease, locked.Id, record =>
        {
            if (record.Status is not (PayoutStatus.Signed or PayoutStatus.Broadcasted)
                || record.WalletSeqno != seqno
                || record.ResignCount != locked.ResignCount
                || !string.Equals(record.SignedBoc, locked.SignedBoc, StringComparison.Ordinal))
            {
                return;
            }

            applied = true;
            record.ResignCount++;
            record.Status = PayoutStatus.Signed;
            record.WalletSeqno = signed.Seqno;
            record.ValidUntilUtc = signed.ValidUntilUtc;
            record.SignedBoc = signed.BocBase64;
            record.ExternalMessageHash = signed.MessageHashHex;
            Track(record, signed.MessageHashHex, signed.MessageHashBase64);
            record.LastSend = SendDisposition.None;
            record.LastBroadcastAtUtc = _time.GetUtcNow();
            record.Attempts++;
            record.LastError = null;
            _logger.LogInformation("Payout {PayoutId} re-signed seqno {Seqno}.", record.Id, signed.Seqno);
        }, cancellationToken).ConfigureAwait(false);
        if (!applied)
            return;

        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        await BroadcastAsync(locked.Id, signed.BocBase64, cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(Guid id, string boc, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.SendBocAsync(boc, cancellationToken).ConfigureAwait(false);
            ChainReached();
            await _store.UpdateAsync(id, record =>
            {
                var from = record.Status;
                record.Status = PayoutStatus.Broadcasted;
                record.LastSend = SendDisposition.Accepted;
                Track(record, response.MessageHash, response.MessageHashNorm);
                record.LastError = null;
                _logger.LogInformation("Payout {PayoutId} {FromStatus} -> Broadcasted.", id, from);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (TonCenterException ex) when (ex.Kind == TonCenterErrorKind.Duplicate)
        {
            ChainReached();
            await _store.UpdateAsync(id, record =>
            {
                record.Status = PayoutStatus.Broadcasted;
                record.LastSend = SendDisposition.Accepted;
                record.LastError = null;
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Payout {PayoutId} message is already known.", id);
        }
        catch (TonCenterException ex) when (ex.Kind == TonCenterErrorKind.Rejected)
        {
            ChainReached();
            await _store.UpdateAsync(id, record =>
            {
                if (record.LastSend != SendDisposition.Accepted)
                    record.LastSend = SendDisposition.Rejected;

                record.LastError = ex.Message;
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Payout {PayoutId} was rejected by TON Center.", id);
        }
        catch (TonCenterException ex) when (IsTransport(ex))
        {
            await _store.UpdateAsync(id, record =>
            {
                record.LastSend = SendDisposition.Uncertain;
                record.LastError = ex.Message;
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Payout {PayoutId} broadcast is uncertain ({Kind}).", id, ex.Kind);
            await NoteOutageAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NoteInsufficientAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        var notify = false;
        await _store.UpdateAsync(payout.Id, record =>
        {
            record.LastError = "Not enough GRAM is available.";
            if (record.InsufficientNotified)
                return;

            record.InsufficientNotified = true;
            notify = true;
        }, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Payout {PayoutId} is waiting for balance.", payout.Id);
        if (!notify)
            return;

        await NotifyAsync(payout, PayoutNoticeKind.InsufficientBalance, "Not enough GRAM is available.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task NoteSignFailureAsync(Guid id, CancellationToken cancellationToken)
    {
        var review = false;
        await _store.UpdateAsync(id, record =>
        {
            record.SignFailures++;
            record.LastError = "Couldn't sign the transfer.";
            review = record.SignFailures >= 3
                && record.Status is not (PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview);
        }, cancellationToken).ConfigureAwait(false);
        if (review)
            await FinishAsync(id, PayoutStatus.NeedsReview, "Couldn't sign the transfer.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(Guid id, PayoutStatus status, string? reason, string? transactionHash, CancellationToken cancellationToken)
    {
        PayoutRecord? snapshot = null;
        var notify = false;
        var notifyOnSuccess = await _store.NotifyOnSuccessAsync(cancellationToken).ConfigureAwait(false);
        await _store.UpdateAsync(id, record =>
        {
            if (record.Status is PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview)
            {
                snapshot = record.Clone();
                return;
            }

            var from = record.Status;
            record.Status = status;
            if (!string.IsNullOrWhiteSpace(transactionHash))
                record.TransactionHash = transactionHash;

            record.LastError = reason;
            if (status == PayoutStatus.Confirmed)
            {
                record.ConfirmedAtUtc = _time.GetUtcNow();
                record.LastError = null;
                notify = notifyOnSuccess && !record.SuccessNotified;
                record.SuccessNotified = true;
            }
            else
            {
                notify = !record.TerminalNotified;
                record.TerminalNotified = true;
            }

            snapshot = record.Clone();
            _logger.LogInformation("Payout {PayoutId} {FromStatus} -> {ToStatus}.", id, from, status);
        }, cancellationToken).ConfigureAwait(false);

        if (!notify || snapshot is null)
            return;

        var kind = status switch
        {
            PayoutStatus.Confirmed => PayoutNoticeKind.Confirmed,
            PayoutStatus.Failed => PayoutNoticeKind.Failed,
            _ => PayoutNoticeKind.NeedsReview
        };
        await NotifyAsync(snapshot, kind, reason, snapshot.TransactionHash, cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyAsync(PayoutRecord payout, PayoutNoticeKind kind, string? detail, string? transactionHash, CancellationToken cancellationToken)
    {
        var explorer = string.IsNullOrWhiteSpace(transactionHash) ? null : _options.TransactionExplorerUrl(transactionHash);
        await _notifier.NotifyAsync(
            new PayoutNotice(
                kind,
                payout.Id,
                GramAmount.FormatGram(payout.AmountNano.ToString(CultureInfo.InvariantCulture)),
                payout.DestinationFriendly,
                detail,
                explorer),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WalletChainSnapshot?> ReadWalletAsync(CancellationToken cancellationToken)
    {
        var response = await _client.GetWalletStatesAsync(_options.Wallet.NonBounceable, cancellationToken).ConfigureAwait(false);
        ChainReached();
        return WalletIdentity.Read(response, _options.Wallet);
    }

    private async Task<string?> ReadDestinationAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        var response = await _client.GetAccountStateAsync(payout.DestinationRaw, cancellationToken).ConfigureAwait(false);
        ChainReached();
        if (response.Accounts is null || response.Accounts.Count == 0)
            return "nonexist";

        foreach (var account in response.Accounts)
        {
            if (!TonAddress.TryParse(account.Address, _options.IsTestnet, out var parsed))
                continue;

            if (!string.Equals(parsed.Raw, payout.DestinationRaw, StringComparison.OrdinalIgnoreCase))
                continue;

            return string.IsNullOrWhiteSpace(account.Status) ? "unknown" : account.Status.Trim().ToLowerInvariant();
        }

        return "nonexist";
    }

    private PayoutObservation Observe(PayoutRecord payout)
    {
        return new PayoutObservation(
            _options.Wallet.Raw,
            _options.IsTestnet,
            payout.DestinationRaw,
            payout.AmountNano,
            payout.Comment,
            payout.WalletSeqno ?? -1,
            payout.ValidUntilUtc ?? DateTimeOffset.MinValue,
            payout.ResignCount,
            payout.LastSend,
            payout.TrackedHashes,
            _time.GetUtcNow());
    }

    private void ApplyChain(ChainCheck check)
    {
        _gate.ApplyChain(check);
        if (!check.Permanent || _loggedBlock)
            return;

        _loggedBlock = true;
        _logger.LogError("Payout signing blocked: {Reason}", check.Problem);
    }

    private void ChainReached()
    {
        _outageSince = null;
        _outageNotified = false;
    }

    private async Task NoteOutageAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        _outageSince ??= now;
        if (_outageNotified || now - _outageSince < _outageAfter)
            return;

        _outageNotified = true;
        await _notifier.NotifyAsync(
            new PayoutNotice(PayoutNoticeKind.TonCenterDown, null, null, null, "TON Center has been unreachable.", null),
            cancellationToken).ConfigureAwait(false);
    }

    private static void Track(PayoutRecord payout, params string?[] hashes)
    {
        foreach (var hash in hashes)
        {
            if (string.IsNullOrWhiteSpace(hash))
                continue;

            if (payout.TrackedHashes.Any(existing => TonHash.Same(existing, hash)))
                continue;

            payout.TrackedHashes.Add(hash.Trim());
        }
    }

    private static bool IsTransport(TonCenterException exception)
    {
        return exception.Kind is TonCenterErrorKind.Unavailable or TonCenterErrorKind.Timeout or TonCenterErrorKind.RateLimited;
    }
}
