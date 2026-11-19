using System.Globalization;
using System.Text;
using GramWalletService.Configuration;
using GramWalletService.Models;
using GramWalletService.Payouts;
using GramWalletService.Ton;
using Telegram.Bot.Types.ReplyMarkups;

namespace GramWalletService.Telegram;

internal static class TelegramScreens
{
    private const int MaxLength = 3900;

    public static (string Text, InlineKeyboardMarkup Keyboard) Menu(AppOptions options, WalletBalanceDto balance)
    {
        var text = new StringBuilder()
            .AppendLine("💎 <b>GRAM Wallet</b>")
            .AppendLine()
            .Append("Network: ").AppendLine(Esc(options.NetworkLabel))
            .Append("Address: <code>").Append(Esc(TonAddress.Shorten(balance.Address))).AppendLine("</code>")
            .Append("Balance: <b>").Append(Esc(balance.Balance)).AppendLine(" GRAM</b>");

        if (!string.Equals(balance.Status, "active", StringComparison.Ordinal))
            text.Append("Status: ").AppendLine(Esc(StatusLabel(balance.Status)));

        return (text.ToString().TrimEnd(), MainMenu());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) MenuUnavailable(AppOptions options, string problem)
    {
        var text = new StringBuilder()
            .AppendLine("💎 <b>GRAM Wallet</b>")
            .AppendLine()
            .Append("Network: ").AppendLine(Esc(options.NetworkLabel))
            .Append("Address: <code>").Append(Esc(TonAddress.Shorten(options.Wallet.NonBounceable))).AppendLine("</code>")
            .AppendLine("Balance: unavailable")
            .AppendLine()
            .Append("⚠️ ").Append(Esc(problem))
            .ToString()
            .TrimEnd();

        return (text, MainMenu());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) Address(WalletAddressDto address)
    {
        var network = address.Network == "testnet" ? "TON Testnet" : "TON Mainnet";
        var text = new StringBuilder()
            .AppendLine("📋 <b>Address</b>")
            .AppendLine()
            .Append("<code>").Append(Esc(address.Address)).AppendLine("</code>")
            .AppendLine()
            .Append("Network: ").AppendLine(Esc(network))
            .AppendLine("Bounceable:")
            .Append("<code>").Append(Esc(address.BounceableAddress)).AppendLine("</code>")
            .AppendLine()
            .Append("<a href=\"").Append(Attr(address.ExplorerUrl)).AppendLine("\">Open in TON Explorer</a>")
            .ToString()
            .TrimEnd();

        return (text, BackOnly());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) Balance(WalletBalanceDto balance)
    {
        var text = new StringBuilder()
            .AppendLine("💰 <b>Balance</b>")
            .AppendLine()
            .Append("<b>").Append(Esc(balance.Balance)).AppendLine(" GRAM</b>")
            .Append("Status: ").AppendLine(Esc(StatusLabel(balance.Status)))
            .Append("Updated: ").AppendLine(Esc(FormatUtc(balance.RetrievedAtUtc)));

        if (balance.Status is "nonexist" or "uninit")
            text.AppendLine().Append("This account is not active on-chain yet.");

        return (text.ToString().TrimEnd(), BackOnly());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) Transactions(TransactionPageDto page)
    {
        var detailed = BuildTransactions(page, fullCounterparties: true);
        var text = detailed.Length <= MaxLength
            ? detailed
            : BuildTransactions(page, fullCounterparties: false);

        return (text, TransactionKeyboard(page));
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) Problem(string message)
    {
        var text = "⚠️ " + Esc(message);
        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("🔄 Refresh", "gw:refresh"),
                InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:menu")
            ]
        ]);
        return (text, keyboard);
    }

    public static InlineKeyboardMarkup MainMenu()
    {
        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("📋 Address", "gw:addr"),
                InlineKeyboardButton.WithCallbackData("💰 Balance", "gw:bal")
            ],
            [
                InlineKeyboardButton.WithCallbackData("💸 Withdraw", "gw:wd")
            ],
            [
                InlineKeyboardButton.WithCallbackData("📜 Transactions", "gw:tx:0"),
                InlineKeyboardButton.WithCallbackData("🔄 Refresh", "gw:refresh")
            ],
            [
                InlineKeyboardButton.WithCallbackData("📤 Payouts", "gw:po:0"),
                InlineKeyboardButton.WithCallbackData("⚙️ Payout Settings", "gw:ps")
            ]
        ]);
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) WithdrawAddress(string network, string? problem)
    {
        var text = new StringBuilder()
            .AppendLine("💸 <b>Withdraw</b>")
            .AppendLine();
        AppendProblem(text, problem);
        text.Append("Network: ").AppendLine(Esc(network))
            .AppendLine()
            .Append("Send the destination address.");
        return (text.ToString().TrimEnd(), CancelOnly());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) WithdrawAmount(
        string address,
        string? balance,
        string maxPayout,
        string? problem)
    {
        var text = new StringBuilder()
            .AppendLine("💸 <b>Withdraw</b>")
            .AppendLine();
        AppendProblem(text, problem);
        text.Append("To: <code>").Append(Esc(address)).AppendLine("</code>");
        if (balance is not null)
            text.Append("Balance: <b>").Append(Esc(balance)).AppendLine(" GRAM</b>");
        text.Append("Max: ").Append(Esc(maxPayout)).AppendLine(" GRAM")
            .AppendLine()
            .Append("How much GRAM?");
        return (text.ToString().TrimEnd(), CancelOnly());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) WithdrawMemo(string address, string amount, string? problem)
    {
        var text = new StringBuilder()
            .AppendLine("💸 <b>Withdraw</b>")
            .AppendLine();
        AppendProblem(text, problem);
        text.Append("To: <code>").Append(Esc(address)).AppendLine("</code>")
            .Append("Amount: <b>").Append(Esc(amount)).AppendLine(" GRAM</b>")
            .AppendLine()
            .Append("Send a memo, or skip it.");
        return (text.ToString().TrimEnd(), SkipOrCancel());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) WithdrawReview(
        string address,
        string amount,
        string? memo,
        bool paused,
        string? problem)
    {
        var text = new StringBuilder()
            .AppendLine("💸 <b>Review withdraw</b>")
            .AppendLine();
        AppendProblem(text, problem);
        text.Append("To: <code>").Append(Esc(address)).AppendLine("</code>")
            .Append("Amount: <b>").Append(Esc(amount)).AppendLine(" GRAM</b>");
        if (string.IsNullOrWhiteSpace(memo))
            text.AppendLine("Memo: none");
        else
            text.Append("Memo: <i>").Append(Esc(memo)).AppendLine("</i>");

        text.AppendLine();
        text.Append(paused
            ? "⏸ Payouts are paused. This waits until you resume."
            : "Approve and it goes out.");
        return (text.ToString().TrimEnd(), ApproveOrCancel());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) WithdrawQueued(
        string amount,
        string address,
        string? memo,
        bool paused)
    {
        var text = new StringBuilder()
            .AppendLine("✅ <b>Withdraw queued</b>")
            .AppendLine()
            .Append("<b>").Append(Esc(amount)).Append("</b> GRAM → <code>").Append(Esc(address)).AppendLine("</code>");
        if (!string.IsNullOrWhiteSpace(memo))
            text.Append("Memo: <i>").Append(Esc(memo)).AppendLine("</i>");

        text.AppendLine();
        text.Append(paused
            ? "⏸ It's queued, and it waits until you resume payouts."
            : "It'll send on its own.");
        return (text.ToString().TrimEnd(), BackOnly());
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) Payouts(PayoutPageDto page)
    {
        var text = new StringBuilder()
            .AppendLine("📤 <b>Payouts</b>")
            .AppendLine();

        if (page.Payouts.Count == 0)
        {
            text.Append(page.Offset == 0 ? "No payouts yet." : "No more payouts on this page.");
            return (text.ToString().TrimEnd(), PayoutKeyboard(page));
        }

        var shown = 0;
        foreach (var payout in page.Payouts)
        {
            var block = FormatPayout(payout, page.Offset + shown + 1);
            var divider = shown == 0 ? "" : "\n";
            if (text.Length + divider.Length + block.Length > MaxLength)
            {
                text.AppendLine("…older rows on this page were cut so the message fits.");
                break;
            }

            text.Append(divider);
            text.AppendLine(block);
            shown++;
        }

        return (text.ToString().TrimEnd(), PayoutKeyboard(page));
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) PayoutSettings(PayoutSettingsSnapshot settings, string? notice)
    {
        var status = settings.Paused ? "Paused ⏸" : "Running ▶️";
        var signing = settings.SigningReady
            ? "Ready ✅"
            : settings.SigningConfigured
                ? "Waiting for the chain check ⏳"
                : "Blocked ⛔";
        var text = new StringBuilder()
            .AppendLine("⚙️ <b>Payout settings</b>")
            .AppendLine()
            .Append("Payouts: ").AppendLine(status)
            .Append("Signing: ").AppendLine(signing)
            .Append("Max payout: <b>").Append(Esc(settings.MaxPayout)).AppendLine(" GRAM</b>")
            .Append("Min reserve: <b>").Append(Esc(settings.MinReserve)).AppendLine(" GRAM</b>")
            .Append("Queued: ").AppendLine(settings.Queued.ToString(CultureInfo.InvariantCulture))
            .Append("Needs review: ").AppendLine(settings.NeedsReview.ToString(CultureInfo.InvariantCulture))
            .Append("Success alerts: ").AppendLine(settings.NotifyOnSuccess ? "On 🔔" : "Off 🔕");

        if (!settings.SigningConfigured && !string.IsNullOrWhiteSpace(settings.SigningProblem))
            text.AppendLine().Append("⚠️ ").Append(Esc(settings.SigningProblem));

        if (!string.IsNullOrWhiteSpace(notice))
            text.AppendLine().Append(Esc(notice));

        var toggle = settings.Paused
            ? InlineKeyboardButton.WithCallbackData("▶️ Resume Payouts", "gw:pr")
            : InlineKeyboardButton.WithCallbackData("⏸ Pause Payouts", "gw:pp");
        var alerts = settings.NotifyOnSuccess
            ? InlineKeyboardButton.WithCallbackData("🔕 Mute success alerts", "gw:pn")
            : InlineKeyboardButton.WithCallbackData("🔔 Success alerts", "gw:pn");
        var keyboard = new InlineKeyboardMarkup(
        [
            [toggle],
            [alerts],
            [
                InlineKeyboardButton.WithCallbackData("🔄 Refresh", "gw:ps"),
                InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:menu")
            ]
        ]);
        return (text.ToString().TrimEnd(), keyboard);
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) ConfirmPause()
    {
        var text = "⏸ <b>Pause payouts?</b>\n\nNew ones stay queued. Anything already sent keeps being tracked.";
        var keyboard = new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("✅ Pause", "gw:py")],
            [InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:ps")]
        ]);
        return (text, keyboard);
    }

    public static (string Text, InlineKeyboardMarkup Keyboard) ConfirmResume()
    {
        var text = "▶️ <b>Resume payouts?</b>\n\nQueued payouts will send on their own.";
        var keyboard = new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("✅ Resume", "gw:ry")],
            [InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:ps")]
        ]);
        return (text, keyboard);
    }

    private static string BuildTransactions(TransactionPageDto page, bool fullCounterparties)
    {
        if (page.Transactions.Count == 0)
        {
            var empty = page.Offset == 0
                ? "📜 <b>Transactions</b>\n\nNothing here yet."
                : "📜 <b>Transactions</b>\n\nNo more transactions on this page.";
            return empty;
        }

        var header = new StringBuilder()
            .AppendLine("📜 <b>Transactions</b>")
            .Append(page.Offset + 1)
            .Append('–')
            .Append(page.Offset + page.Transactions.Count)
            .AppendLine()
            .AppendLine();

        var text = new StringBuilder(header.ToString());
        var shown = 0;
        foreach (var transaction in page.Transactions)
        {
            var block = FormatTransaction(transaction, page.Offset + shown + 1, fullCounterparties);
            var divider = shown == 0 ? "" : "----------------\n";
            if (text.Length + divider.Length + block.Length > MaxLength)
            {
                text.AppendLine("…older rows on this page were cut so the message fits.");
                break;
            }

            text.Append(divider);
            text.AppendLine(block);
            shown++;
        }

        return text.ToString().TrimEnd();
    }

    private static string FormatTransaction(WalletTransactionDto transaction, int number, bool fullCounterparties)
    {
        var text = new StringBuilder()
            .Append("<b>").Append(number.ToString(CultureInfo.InvariantCulture)).Append(".</b> ");

        if (transaction.TimestampUtc is DateTimeOffset timestamp)
            text.AppendLine(Esc(FormatUtc(timestamp)));
        else
            text.AppendLine("time unavailable");

        var visible = VisibleMessages(transaction.Messages);
        if (visible.Count == 0)
        {
            text.AppendLine("• No GRAM transfer");
        }
        else
        {
            var shown = 0;
            foreach (var message in visible)
            {
                if (shown == 6)
                {
                    text.Append('•').Append(' ').Append((visible.Count - shown).ToString(CultureInfo.InvariantCulture))
                        .AppendLine(" more messages in this transaction");
                    break;
                }

                text.Append(Icon(message.Direction)).Append(' ').Append(Label(message.Direction));
                if (message.Amount is not null)
                    text.Append(" · ").Append(Esc(message.Amount)).Append(" GRAM");
                if (message.Bounced)
                    text.Append(" · bounced");
                text.AppendLine();

                if (!string.IsNullOrWhiteSpace(message.Counterparty))
                {
                    var counterparty = fullCounterparties ? message.Counterparty : TonAddress.Shorten(message.Counterparty);
                    text.Append(CounterpartyLabel(message.Direction))
                        .Append(": <code>")
                        .Append(Esc(counterparty))
                        .AppendLine("</code>");
                }

                shown++;
            }
        }

        if (transaction.Fee is not null)
            text.Append("Fee: ").Append(Esc(transaction.Fee)).AppendLine(" GRAM");

        if (transaction.Aborted == true)
            text.AppendLine("Result: aborted");

        text.Append("Hash: <code>").Append(Esc(transaction.Hash)).AppendLine("</code>");
        text.Append("<a href=\"").Append(Attr(transaction.ExplorerUrl)).Append("\">Explorer</a>");
        return text.ToString().TrimEnd();
    }

    private static List<WalletMessageDto> VisibleMessages(IReadOnlyList<WalletMessageDto> messages)
    {
        var valued = messages.Where(message => message.Amount is not null).ToList();
        if (valued.Count > 0)
            return valued;

        if (messages.Count == 0)
            return [];

        return [messages[0]];
    }

    private static InlineKeyboardMarkup TransactionKeyboard(TransactionPageDto page)
    {
        var navigation = new List<InlineKeyboardButton>();
        if (page.Offset > 0)
        {
            var previous = Math.Max(0, page.Offset - page.Limit);
            navigation.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", "gw:tx:" + previous.ToString(CultureInfo.InvariantCulture)));
        }

        if (page.HasMore)
        {
            var next = page.Offset + page.Limit;
            if (next <= 1_000_000)
                navigation.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", "gw:tx:" + next.ToString(CultureInfo.InvariantCulture)));
        }

        if (navigation.Count == 0)
            return BackOnly();

        return new InlineKeyboardMarkup(
        [
            navigation,
            [InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:menu")]
        ]);
    }

    private static string FormatPayout(PayoutDto payout, int number)
    {
        var text = new StringBuilder()
            .Append("<b>").Append(number.ToString(CultureInfo.InvariantCulture)).Append(".</b> ")
            .Append(PayoutIcon(payout.Status)).Append(' ')
            .Append(Esc(payout.Amount)).AppendLine(" GRAM")
            .Append("To: <code>").Append(Esc(TonAddress.Shorten(payout.Address))).AppendLine("</code>")
            .Append("Status: ").AppendLine(Esc(payout.Status))
            .Append(Esc(FormatUtc(payout.CreatedAtUtc)));

        if (!string.IsNullOrWhiteSpace(payout.TransactionHash))
        {
            text.AppendLine()
                .Append("Hash: <code>").Append(Esc(payout.TransactionHash)).AppendLine("</code>");
            if (!string.IsNullOrWhiteSpace(payout.ExplorerUrl))
                text.Append("<a href=\"").Append(Attr(payout.ExplorerUrl)).Append("\">Tonviewer</a>");
        }

        return text.ToString().TrimEnd();
    }

    private static string PayoutIcon(string status)
    {
        return status switch
        {
            "Queued" => "⏳",
            "Signed" => "✍️",
            "Broadcasted" => "📡",
            "Confirmed" => "✅",
            "Failed" => "❌",
            "NeedsReview" => "👀",
            _ => "•"
        };
    }

    private static InlineKeyboardMarkup PayoutKeyboard(PayoutPageDto page)
    {
        var navigation = new List<InlineKeyboardButton>();
        if (page.Offset > 0)
        {
            var previous = Math.Max(0, page.Offset - page.Limit);
            navigation.Add(InlineKeyboardButton.WithCallbackData("⬅️ Previous", "gw:po:" + previous.ToString(CultureInfo.InvariantCulture)));
        }

        if (page.HasMore)
        {
            var next = page.Offset + page.Limit;
            if (next <= 1_000_000)
                navigation.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", "gw:po:" + next.ToString(CultureInfo.InvariantCulture)));
        }

        if (navigation.Count == 0)
            return BackOnly();

        return new InlineKeyboardMarkup(
        [
            navigation,
            [InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:menu")]
        ]);
    }

    private static InlineKeyboardMarkup BackOnly()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("🔙 Back", "gw:menu")]
        ]);
    }

    private static InlineKeyboardMarkup CancelOnly()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("❌ Cancel", "gw:wx")]
        ]);
    }

    private static InlineKeyboardMarkup SkipOrCancel()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("⏭ Skip", "gw:ws")],
            [InlineKeyboardButton.WithCallbackData("❌ Cancel", "gw:wx")]
        ]);
    }

    private static InlineKeyboardMarkup ApproveOrCancel()
    {
        return new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("✅ Approve", "gw:wa")],
            [InlineKeyboardButton.WithCallbackData("❌ Cancel", "gw:wx")]
        ]);
    }

    private static void AppendProblem(StringBuilder text, string? problem)
    {
        if (string.IsNullOrWhiteSpace(problem))
            return;

        text.Append("⚠️ ").AppendLine(Esc(problem)).AppendLine();
    }

    private static string StatusLabel(string status)
    {
        return status switch
        {
            "active" => "Active ✅",
            "uninit" => "Uninitialized",
            "frozen" => "Frozen",
            "nonexist" => "Not on-chain yet",
            _ => status
        };
    }

    private static string Icon(string direction)
    {
        return direction switch
        {
            MessageDirections.Incoming => "⬇️",
            MessageDirections.Outgoing => "⬆️",
            _ => "•"
        };
    }

    private static string Label(string direction)
    {
        return direction switch
        {
            MessageDirections.Incoming => "Incoming",
            MessageDirections.Outgoing => "Outgoing",
            _ => "Other"
        };
    }

    private static string CounterpartyLabel(string direction)
    {
        return direction switch
        {
            MessageDirections.Incoming => "From",
            MessageDirections.Outgoing => "To",
            _ => "Account"
        };
    }

    private static string FormatUtc(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
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
