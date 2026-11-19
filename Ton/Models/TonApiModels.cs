using System.Text.Json.Serialization;
using GramWalletService.Ton;

namespace GramWalletService.Ton.Models;

internal sealed class AccountStatesResponse
{
    [JsonPropertyName("accounts")]
    public List<AccountStateModel>? Accounts { get; set; }

    [JsonPropertyName("address_book")]
    public Dictionary<string, AddressBookEntry>? AddressBook { get; set; }
}

internal sealed class AccountStateModel
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("balance")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? Balance { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal sealed class TransactionsResponse
{
    [JsonPropertyName("transactions")]
    public List<TransactionModel>? Transactions { get; set; }

    [JsonPropertyName("address_book")]
    public Dictionary<string, AddressBookEntry>? AddressBook { get; set; }
}

internal sealed class AddressBookEntry
{
    [JsonPropertyName("user_friendly")]
    public string? UserFriendly { get; set; }

    [JsonPropertyName("interfaces")]
    public List<string>? Interfaces { get; set; }
}

internal sealed class TransactionModel
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("lt")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? Lt { get; set; }

    [JsonPropertyName("now")]
    [JsonConverter(typeof(FlexibleNullableInt64Converter))]
    public long? Now { get; set; }

    [JsonPropertyName("total_fees")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? TotalFees { get; set; }

    [JsonPropertyName("orig_status")]
    public string? OrigStatus { get; set; }

    [JsonPropertyName("end_status")]
    public string? EndStatus { get; set; }

    [JsonPropertyName("description")]
    public TransactionDescriptionModel? Description { get; set; }

    [JsonPropertyName("emulated")]
    public bool? Emulated { get; set; }

    [JsonPropertyName("account")]
    public string? Account { get; set; }

    [JsonPropertyName("in_msg")]
    public MessageModel? InMsg { get; set; }

    [JsonPropertyName("out_msgs")]
    public List<MessageModel>? OutMsgs { get; set; }
}

internal sealed class TransactionDescriptionModel
{
    [JsonPropertyName("aborted")]
    public bool? Aborted { get; set; }

    [JsonPropertyName("compute_ph")]
    public ComputePhaseModel? ComputePh { get; set; }

    [JsonPropertyName("action")]
    public ActionPhaseModel? Action { get; set; }
}

internal sealed class ComputePhaseModel
{
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("skipped")]
    public bool? Skipped { get; set; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; set; }
}

internal sealed class ActionPhaseModel
{
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("valid")]
    public bool? Valid { get; set; }

    [JsonPropertyName("no_funds")]
    public bool? NoFunds { get; set; }

    [JsonPropertyName("result_code")]
    public int? ResultCode { get; set; }

    [JsonPropertyName("msgs_created")]
    public int? MsgsCreated { get; set; }
}

internal sealed class MessageModel
{
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("value")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? Value { get; set; }

    [JsonPropertyName("opcode")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? Opcode { get; set; }

    [JsonPropertyName("decoded_opcode")]
    public string? DecodedOpcode { get; set; }

    [JsonPropertyName("bounced")]
    public bool? Bounced { get; set; }
}

internal sealed class WalletStatesResponse
{
    [JsonPropertyName("wallets")]
    public List<WalletStateModel>? Wallets { get; set; }

    [JsonPropertyName("address_book")]
    public Dictionary<string, AddressBookEntry>? AddressBook { get; set; }
}

internal sealed class WalletStateModel
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("is_wallet")]
    public bool? IsWallet { get; set; }

    [JsonPropertyName("wallet_type")]
    public string? WalletType { get; set; }

    [JsonPropertyName("seqno")]
    public int? Seqno { get; set; }

    [JsonPropertyName("wallet_id")]
    public long? WalletId { get; set; }

    [JsonPropertyName("balance")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? Balance { get; set; }

    [JsonPropertyName("is_signature_allowed")]
    public bool? IsSignatureAllowed { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("code_hash")]
    public string? CodeHash { get; set; }
}

internal sealed class ActionsResponse
{
    [JsonPropertyName("actions")]
    public List<ActionModel>? Actions { get; set; }
}

internal sealed class ActionModel
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("finality")]
    public string? Finality { get; set; }

    [JsonPropertyName("details")]
    public ActionDetailsModel? Details { get; set; }
}

internal sealed class ActionDetailsModel
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("value")]
    [JsonConverter(typeof(JsonAnyStringConverter))]
    public string? Value { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("encrypted")]
    public bool? Encrypted { get; set; }
}

internal sealed class SendMessageResponse
{
    [JsonPropertyName("message_hash")]
    public string? MessageHash { get; set; }

    [JsonPropertyName("message_hash_norm")]
    public string? MessageHashNorm { get; set; }
}
