using System.Text.Json;
using GramWalletService.Models;
using GramWalletService.Payouts;
using GramWalletService.Ton;

namespace GramWalletService.Services;

internal sealed class BalanceWatcher : BackgroundService
{
    private const int PageSize = 20;
    private const int MaxPages = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions CursorJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly GramWalletService _wallet;
    private readonly IPayoutNotifier _notifier;
    private readonly ILogger<BalanceWatcher> _logger;
    private readonly string _cursorPath;

    public BalanceWatcher(
        GramWalletService wallet,
        IPayoutNotifier notifier,
        IHostEnvironment environment,
        ILogger<BalanceWatcher> logger)
    {
        _wallet = wallet;
        _notifier = notifier;
        _logger = logger;
        _cursorPath = Path.Combine(environment.ContentRootPath, "data", "deposit-cursor.json");
    }

    public static void SelfCheck()
    {
        if (CompareLogicalTime("99", "100") >= 0
            || CompareLogicalTime("100", "100") != 0
            || CompareLogicalTime("1000", "999") <= 0
            || CompareLogicalTime("00010", "10") != 0
            || CompareLogicalTime("10", "2") <= 0)
        {
            throw new InvalidOperationException("Logical time comparison self-check failed.");
        }

        var skipped = Credits(Sample(aborted: true, Incoming("1", "UQFrom", bounced: false)));
        var bounced = Credits(Sample(aborted: false, Incoming("1", "UQFrom", bounced: true)));
        var outgoing = Credits(Sample(
            aborted: false,
            new WalletMessageDto(MessageDirections.Outgoing, "1", "1000000000", "UQTo", null, false)));
        var toppedUp = Credits(Sample(aborted: false, Incoming("1.5", "UQFrom", bounced: false)));
        if (skipped.Count != 0
            || bounced.Count != 0
            || outgoing.Count != 0
            || toppedUp.Count != 1
            || toppedUp[0].Amount != "1.5"
            || toppedUp[0].From != "UQFrom")
        {
            throw new InvalidOperationException("Deposit scan self-check failed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("Balance watcher started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (TonCenterException ex)
                {
                    _logger.LogWarning("Balance check failed ({Kind}).", ex.Kind);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Balance watcher failed ({Type}).", ex.GetType().Name);
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _logger.LogInformation("Balance watcher stopped.");
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var cursor = ReadCursor();
        if (cursor is null)
        {
            var latest = await _wallet.GetTransactionsAsync(1, 0, cancellationToken).ConfigureAwait(false);
            var logicalTime = latest.Transactions.Count == 0 ? "0" : latest.Transactions[0].LogicalTime;
            SaveCursor(logicalTime);
            _logger.LogInformation("Balance watcher armed at logical time {LogicalTime}. Older transfers stay quiet.", logicalTime);
            return;
        }

        var fresh = new List<WalletTransactionDto>();
        var reachedCursor = false;
        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var page = await _wallet.GetTransactionsAsync(PageSize, pageIndex * PageSize, cancellationToken).ConfigureAwait(false);
            if (page.Transactions.Count == 0)
            {
                reachedCursor = true;
                break;
            }

            foreach (var transaction in page.Transactions)
            {
                if (CompareLogicalTime(transaction.LogicalTime, cursor) <= 0)
                {
                    reachedCursor = true;
                    break;
                }

                fresh.Add(transaction);
            }

            if (reachedCursor || !page.HasMore)
            {
                reachedCursor = true;
                break;
            }
        }

        if (fresh.Count == 0)
            return;

        if (!reachedCursor)
            _logger.LogWarning("Balance watcher missed older top-ups after a long gap. New ones from here are still announced.");

        fresh.Reverse();
        string? balance = null;
        if (fresh.Any(transaction => Credits(transaction).Count > 0))
            balance = await TryBalanceAsync(cancellationToken).ConfigureAwait(false);

        foreach (var transaction in fresh)
        {
            var credits = Credits(transaction);
            if (credits.Count > 0)
            {
                await _notifier.NotifyDepositAsync(
                    new DepositNotice(credits, balance, transaction.ExplorerUrl),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Wallet topped up by {Amount} GRAM.",
                    string.Join(", ", credits.Select(credit => credit.Amount)));
            }

            SaveCursor(transaction.LogicalTime);
        }
    }

    private async Task<string?> TryBalanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var balance = await _wallet.GetBalanceAsync(cancellationToken).ConfigureAwait(false);
            return balance.Balance;
        }
        catch (TonCenterException ex)
        {
            _logger.LogWarning("Balance after a top-up could not be read ({Kind}).", ex.Kind);
            return null;
        }
    }

    private string? ReadCursor()
    {
        if (!File.Exists(_cursorPath))
            return null;

        try
        {
            var file = JsonSerializer.Deserialize<CursorFile>(File.ReadAllText(_cursorPath), CursorJson);
            if (file is null || !GramAmount.TryNormalizeNano(file.LogicalTime, out var logicalTime) || logicalTime.StartsWith('-'))
            {
                _logger.LogWarning("Balance cursor was unreadable. Older transfers stay quiet.");
                return null;
            }

            var trimmed = logicalTime.TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Balance cursor could not be read ({Type}). Older transfers stay quiet.", ex.GetType().Name);
            return null;
        }
    }

    private void SaveCursor(string logicalTime)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cursorPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(new CursorFile(logicalTime), CursorJson);
            var temporary = _cursorPath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _cursorPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Balance cursor could not be saved ({Type}).", ex.GetType().Name);
        }
    }

    private static int CompareLogicalTime(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        var length = a.Length.CompareTo(b.Length);
        return length != 0 ? length : string.CompareOrdinal(a, b);
    }

    private static string Normalize(string value)
    {
        if (!GramAmount.TryNormalizeNano(value, out var normalized) || normalized.StartsWith('-'))
            return "0";

        var trimmed = normalized.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static IReadOnlyList<DepositCredit> Credits(WalletTransactionDto transaction)
    {
        if (transaction.Aborted == true)
            return [];

        var credits = new List<DepositCredit>();
        foreach (var message in transaction.Messages)
        {
            if (message.Direction != MessageDirections.Incoming || message.Bounced || message.Amount is null)
                continue;

            credits.Add(new DepositCredit(message.Amount, message.Counterparty));
        }

        return credits;
    }

    private static WalletTransactionDto Sample(bool? aborted, params WalletMessageDto[] messages)
    {
        return new WalletTransactionDto("hash", "1", null, null, null, aborted, null, null, "https://tonviewer.com/transaction/hash", messages);
    }

    private static WalletMessageDto Incoming(string amount, string from, bool bounced)
    {
        return new WalletMessageDto(MessageDirections.Incoming, amount, "1000000000", from, null, bounced);
    }

    private sealed record CursorFile(string LogicalTime);
}
