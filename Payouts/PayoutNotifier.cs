using System.Text;
using GramWalletService.Configuration;
using GramWalletService.Ton;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GramWalletService.Payouts;

internal sealed class TelegramPayoutNotifier : IPayoutNotifier
{
    private readonly ITelegramBotClient _bot;
    private readonly AppOptions _options;
    private readonly ILogger<TelegramPayoutNotifier> _logger;

    public TelegramPayoutNotifier(ITelegramBotClient bot, AppOptions options, ILogger<TelegramPayoutNotifier> logger)
    {
        _bot = bot;
        _options = options;
        _logger = logger;
    }

    public Task NotifyAsync(PayoutNotice notice, CancellationToken cancellationToken)
    {
        return SendAsync(Text(notice), cancellationToken);
    }

    public Task NotifyDepositAsync(DepositNotice notice, CancellationToken cancellationToken)
    {
        return SendAsync(TopUp(notice), cancellationToken);
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _bot.SendMessage(
                chatId: _options.TelegramAdminId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Couldn't send a payout notice ({Type}).", ex.GetType().Name);
        }
    }

    private static string Text(PayoutNotice notice)
    {
        var amount = Esc(notice.Amount ?? "?");
        var destination = Esc(notice.Destination is null ? "?" : TonAddress.Shorten(notice.Destination));
        var detail = string.IsNullOrWhiteSpace(notice.Detail) ? null : Esc(notice.Detail);
        return notice.Kind switch
        {
            PayoutNoticeKind.Confirmed => Confirmed(amount, destination, notice.ExplorerUrl),
            PayoutNoticeKind.Failed => "❌ <b>Payout failed</b>\n\n" + amount + " GRAM → <code>" + destination + "</code>"
                + (detail is null ? "" : "\n" + detail),
            PayoutNoticeKind.NeedsReview => "👀 <b>A payout needs a look</b>\n\n" + amount + " GRAM → <code>" + destination + "</code>\n"
                + (detail ?? "I couldn't prove it landed, so I did not send it again."),
            PayoutNoticeKind.InsufficientBalance => "⚠️ <b>Not enough GRAM</b>\n\n" + amount + " GRAM → <code>" + destination + "</code>\nIt stays queued until the wallet can cover it.",
            PayoutNoticeKind.Queued => Queued(amount, destination, detail),
            _ => "⚠️ TON Center has been unreachable for a few minutes. Payouts are waiting."
        };
    }

    private static string Queued(string amount, string destination, string? comment)
    {
        var text = "⏳ <b>Payout queued</b>\n\n" + amount + " GRAM → <code>" + destination + "</code>";
        if (comment is not null)
            text += "\n<i>" + comment + "</i>";

        return text;
    }

    private static string TopUp(DepositNotice notice)
    {
        var text = new StringBuilder()
            .AppendLine("💰 <b>Balance topped up</b>")
            .AppendLine();

        foreach (var credit in notice.Credits)
        {
            text.Append('+').Append(Esc(credit.Amount)).Append(" GRAM");
            if (!string.IsNullOrWhiteSpace(credit.From))
                text.Append(" from <code>").Append(Esc(TonAddress.Shorten(credit.From))).Append("</code>");

            text.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(notice.Balance))
            text.Append("Balance is <b>").Append(Esc(notice.Balance)).AppendLine(" GRAM</b> now");

        if (!string.IsNullOrWhiteSpace(notice.ExplorerUrl))
            text.Append("<a href=\"").Append(Attr(notice.ExplorerUrl)).Append("\">Tonviewer</a>");

        return text.ToString().TrimEnd();
    }

    private static string Confirmed(string amount, string destination, string? explorerUrl)
    {
        var text = "✅ <b>Payout sent</b>\n\n" + amount + " GRAM → <code>" + destination + "</code>";
        if (!string.IsNullOrWhiteSpace(explorerUrl))
            text += "\n<a href=\"" + Esc(explorerUrl).Replace("\"", "&quot;", StringComparison.Ordinal) + "\">Tonviewer</a>";

        return text;
    }

    private static string Esc(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string Attr(string value)
    {
        return Esc(value).Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}

internal sealed class LogPayoutNotifier : IPayoutNotifier
{
    private readonly ILogger<LogPayoutNotifier> _logger;

    public LogPayoutNotifier(ILogger<LogPayoutNotifier> logger)
    {
        _logger = logger;
    }

    public Task NotifyAsync(PayoutNotice notice, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Payout notice {Kind} for {PayoutId}.", notice.Kind, notice.PayoutId);
        return Task.CompletedTask;
    }

    public Task NotifyDepositAsync(DepositNotice notice, CancellationToken cancellationToken)
    {
        var amount = notice.Credits.Count == 0 ? "?" : notice.Credits[0].Amount;
        _logger.LogInformation("Deposit notice for {Amount} GRAM.", amount);
        return Task.CompletedTask;
    }
}
