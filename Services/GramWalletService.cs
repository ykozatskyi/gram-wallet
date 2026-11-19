using System.Text.Json;
using GramWalletService.Configuration;
using GramWalletService.Models;
using GramWalletService.Ton;
using GramWalletService.Ton.Models;

namespace GramWalletService.Services;

public sealed class GramWalletService
{
    public const int MaxTransactionLimit = 50;
    public const int TelegramPageSize = 10;

    private readonly TonCenterClient _client;
    private readonly AppOptions _options;
    private readonly WalletAddressDto _address;
    private readonly ILogger<GramWalletService> _logger;

    public GramWalletService(TonCenterClient client, AppOptions options, ILogger<GramWalletService> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _address = new WalletAddressDto(
            options.Wallet.NonBounceable,
            options.Wallet.Bounceable,
            options.Wallet.Raw,
            options.Network,
            options.AddressExplorerUrl);
    }

    public static void SelfCheck()
    {
        var own = new string('A', 64);
        var other = new string('B', 64);
        var wallet = TonAddress.Parse("0:" + own, rawIsTestOnly: false);
        var json = """
        {
          "transactions": [
            {
              "hash": "out-hash",
              "lt": "100",
              "now": 1700000000,
              "total_fees": "182",
              "orig_status": "active",
              "end_status": "active",
              "description": { "aborted": false, "type": "ord" },
              "in_msg": { "source": null, "destination": "0:OWN", "value": "0", "bounced": false },
              "out_msgs": [
                { "source": "0:OWN", "destination": "0:OTHER", "value": "1500000000", "bounced": false },
                { "source": "0:OWN", "destination": "0:OTHER2", "value": "1", "bounced": false }
              ]
            },
            {
              "hash": "in-hash",
              "lt": "90",
              "now": 1700000100,
              "total_fees": "0",
              "in_msg": {
                "source": "0:OTHER",
                "destination": "0:OWN",
                "value": "1000000000",
                "bounced": true,
                "decoded_opcode": "text_comment"
              },
              "out_msgs": []
            }
          ],
          "address_book": {
            "0:OTHER": { "user_friendly": "UQCounterparty" }
          }
        }
        """.Replace("OWN", own, StringComparison.Ordinal)
           .Replace("OTHER2", new string('C', 64), StringComparison.Ordinal)
           .Replace("OTHER", other, StringComparison.Ordinal);

        var payload = JsonSerializer.Deserialize<TransactionsResponse>(json, TonJson.Options)
            ?? throw new InvalidOperationException("Transaction parser self-check could not read the fixture.");
        var mapped = MapTransactions(payload, wallet, testnet: false, explorerUrl: static hash => "https://tonviewer.com/transaction/" + hash);

        if (mapped.Count != 2)
            throw new InvalidOperationException("Transaction parser self-check lost a transaction.");

        var outgoing = mapped[0];
        if (outgoing.Messages.Count != 3
            || outgoing.Messages[0].Direction != MessageDirections.Other
            || outgoing.Messages[0].Amount is not null
            || outgoing.Messages[1].Direction != MessageDirections.Outgoing
            || outgoing.Messages[1].Amount != "1.5"
            || outgoing.Messages[1].Counterparty != "UQCounterparty"
            || outgoing.Messages[2].Direction != MessageDirections.Outgoing
            || outgoing.Messages[2].Amount != "0.000000001"
            || outgoing.Fee != "0.000000182"
            || outgoing.LogicalTime != "100"
            || outgoing.TimestampUtc != DateTimeOffset.FromUnixTimeSeconds(1700000000))
        {
            throw new InvalidOperationException("Transaction parser self-check misclassified a multi-message transaction.");
        }

        var incoming = mapped[1];
        if (incoming.Messages.Count != 1
            || incoming.Messages[0].Direction != MessageDirections.Incoming
            || incoming.Messages[0].Amount != "1"
            || incoming.Messages[0].Bounced
                != true
            || incoming.Messages[0].Opcode != "text_comment"
            || incoming.Fee != "0")
        {
            throw new InvalidOperationException("Transaction parser self-check misclassified an incoming transfer.");
        }

        var emptyBalance = MapBalance(new AccountStatesResponse { Accounts = [] }, wallet, "mainnet");
        if (emptyBalance.Balance != "0" || emptyBalance.Status != "nonexist" || emptyBalance.Initialized)
            throw new InvalidOperationException("Empty account self-check failed.");

        var uninit = MapBalance(new AccountStatesResponse
        {
            Accounts =
            [
                new AccountStateModel
                {
                    Address = wallet.Raw,
                    Balance = "1000000000",
                    Status = "uninit"
                }
            ]
        }, wallet, "mainnet");
        if (uninit.Balance != "1" || uninit.BalanceNano != "1000000000" || uninit.Initialized || uninit.Status != "uninit")
            throw new InvalidOperationException("Uninitialized account self-check failed.");
    }

    public Task<WalletAddressDto> GetAddressAsync()
    {
        return Task.FromResult(_address);
    }

    public async Task<WalletBalanceDto> GetBalanceAsync(CancellationToken cancellationToken)
    {
        var payload = await _client.GetAccountStateAsync(_address.Address, cancellationToken).ConfigureAwait(false);
        if (payload.Accounts is null)
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unexpected account response.");

        var balance = MapBalance(payload, _options.Wallet, _options.Network);
        _logger.LogDebug("Wallet status is {Status}.", balance.Status);
        return balance;
    }

    public async Task<TransactionPageDto> GetTransactionsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxTransactionLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 50.");

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be zero or greater.");

        var payload = await _client.GetTransactionsAsync(_address.Address, limit + 1, offset, cancellationToken).ConfigureAwait(false);
        if (payload.Transactions is null)
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unexpected transaction response.");

        var hasMore = payload.Transactions.Count > limit;
        if (payload.Transactions.Count > limit)
            payload.Transactions = payload.Transactions.Take(limit).ToList();

        var transactions = MapTransactions(
            payload,
            _options.Wallet,
            _options.IsTestnet,
            _options.TransactionExplorerUrl);
        _logger.LogDebug("Loaded {Count} transactions at offset {Offset}.", transactions.Count, offset);

        return new TransactionPageDto(limit, offset, hasMore, transactions);
    }

    private static WalletBalanceDto MapBalance(AccountStatesResponse response, TonAddress wallet, string network)
    {
        var accounts = response.Accounts ?? throw new TonCenterException(
            TonCenterErrorKind.InvalidResponse,
            "TON Center returned an unexpected account response.");

        if (accounts.Count == 0)
        {
            return new WalletBalanceDto(
                wallet.NonBounceable,
                network,
                "0",
                "0",
                "nonexist",
                false,
                DateTimeOffset.UtcNow);
        }

        AccountStateModel? account = null;
        foreach (var candidate in accounts)
        {
            if (string.IsNullOrWhiteSpace(candidate.Address))
            {
                account ??= candidate;
                continue;
            }

            if (!TonAddress.TryParse(candidate.Address, wallet.IsTestOnly, out var parsed)
                || !string.Equals(parsed.Raw, wallet.Raw, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            account = candidate;
            break;
        }

        if (account is null)
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned a different account.");

        if (!GramAmount.TryNormalizeNano(account.Balance, out var nano) || nano.StartsWith('-'))
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unreadable balance.");

        var status = string.IsNullOrWhiteSpace(account.Status) ? "unknown" : account.Status.Trim().ToLowerInvariant();
        var initialized = status is "active" or "frozen";
        return new WalletBalanceDto(
            wallet.NonBounceable,
            network,
            GramAmount.FormatGram(nano),
            nano,
            status,
            initialized,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<WalletTransactionDto> MapTransactions(
        TransactionsResponse response,
        TonAddress wallet,
        bool testnet,
        Func<string, string> explorerUrl)
    {
        var book = FriendlyBook(response.AddressBook);
        var transactions = new List<WalletTransactionDto>(response.Transactions?.Count ?? 0);
        foreach (var transaction in response.Transactions ?? [])
        {
            if (string.IsNullOrWhiteSpace(transaction.Hash) || string.IsNullOrWhiteSpace(transaction.Lt))
                throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned a transaction without a hash or logical time.");

            if (!GramAmount.TryNormalizeNano(transaction.Lt, out var logicalTime) || logicalTime.StartsWith('-'))
                throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unreadable logical time.");

            string? fee = null;
            string? feeNano = null;
            if (!string.IsNullOrWhiteSpace(transaction.TotalFees))
            {
                if (!GramAmount.TryNormalizeNano(transaction.TotalFees, out var parsedFee) || parsedFee.StartsWith('-'))
                    throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unreadable fee.");

                feeNano = parsedFee;
                fee = GramAmount.FormatGram(parsedFee);
            }

            DateTimeOffset? timestamp = null;
            if (transaction.Now is long unix)
            {
                if (unix < 0)
                    throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unreadable timestamp.");

                timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            transactions.Add(new WalletTransactionDto(
                transaction.Hash,
                logicalTime,
                timestamp,
                fee,
                feeNano,
                transaction.Description?.Aborted,
                BlankToNull(transaction.OrigStatus),
                BlankToNull(transaction.EndStatus),
                explorerUrl(transaction.Hash),
                MapMessages(transaction, wallet, testnet, book)));
        }

        return transactions;
    }

    private static IReadOnlyList<WalletMessageDto> MapMessages(
        TransactionModel transaction,
        TonAddress wallet,
        bool testnet,
        IReadOnlyDictionary<string, string> book)
    {
        var messages = new List<WalletMessageDto>();
        if (transaction.InMsg is not null)
            messages.Add(MapMessage(transaction.InMsg, incoming: true, wallet, testnet, book));

        if (transaction.OutMsgs is not null)
        {
            foreach (var message in transaction.OutMsgs)
                messages.Add(MapMessage(message, incoming: false, wallet, testnet, book));
        }

        return messages;
    }

    private static WalletMessageDto MapMessage(
        MessageModel message,
        bool incoming,
        TonAddress wallet,
        bool testnet,
        IReadOnlyDictionary<string, string> book)
    {
        var hasValue = TryPositiveNano(message.Value, out var nano);
        string direction;
        string? counterpartyRaw;

        if (incoming)
        {
            counterpartyRaw = message.Source;
            var externalOrSelf = string.IsNullOrWhiteSpace(message.Source) || IsOwn(message.Source, wallet, testnet);
            if (externalOrSelf || !hasValue)
                direction = MessageDirections.Other;
            else
                direction = MessageDirections.Incoming;
        }
        else
        {
            counterpartyRaw = message.Destination;
            direction = hasValue ? MessageDirections.Outgoing : MessageDirections.Other;
        }

        return new WalletMessageDto(
            direction,
            hasValue ? GramAmount.FormatGram(nano) : null,
            hasValue ? nano : null,
            ResolveCounterparty(counterpartyRaw, wallet, testnet, book),
            Opcode(message),
            message.Bounced == true);
    }

    private static bool TryPositiveNano(string? value, out string nano)
    {
        nano = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!GramAmount.TryNormalizeNano(value, out nano))
            throw new TonCenterException(TonCenterErrorKind.InvalidResponse, "TON Center returned an unreadable amount.");

        return !nano.StartsWith('-') && nano != "0";
    }

    private static bool IsOwn(string address, TonAddress wallet, bool testnet)
    {
        return TonAddress.TryParse(address, testnet, out var parsed)
            && string.Equals(parsed.Raw, wallet.Raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCounterparty(
        string? address,
        TonAddress wallet,
        bool testnet,
        IReadOnlyDictionary<string, string> book)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        if (book.TryGetValue(address, out var friendly) && !string.IsNullOrWhiteSpace(friendly))
            return friendly;

        if (TonAddress.TryParse(address, testnet || wallet.IsTestOnly, out var parsed))
            return parsed.NonBounceable;

        return address;
    }

    private static Dictionary<string, string> FriendlyBook(Dictionary<string, AddressBookEntry>? book)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (book is null)
            return map;

        foreach (var (key, value) in book)
        {
            if (!string.IsNullOrWhiteSpace(value.UserFriendly))
                map[key] = value.UserFriendly;
        }

        return map;
    }

    private static string? Opcode(MessageModel message)
    {
        if (!string.IsNullOrWhiteSpace(message.DecodedOpcode))
            return message.DecodedOpcode.Trim();

        if (string.IsNullOrWhiteSpace(message.Opcode) || message.Opcode == "0")
            return null;

        return message.Opcode.Trim();
    }

    private static string? BlankToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
