using System.Numerics;
using GramWalletService.Configuration;
using Ton.Contracts.Wallets.V5;
using Ton.Core.Addresses;
using Ton.Core.Boc;
using Ton.Core.Types;
using Ton.Crypto.Mnemonic;

namespace GramWalletService.Ton;

internal sealed class TonWalletSigner : IWalletSigner
{
    public const int MainnetWalletId = 2147483409;

    private readonly WalletV5R1? _wallet;
    private readonly byte[]? _secretKey;
    private readonly ILogger<TonWalletSigner> _logger;

    private TonWalletSigner(WalletLocalIdentity identity, WalletV5R1? wallet, byte[]? secretKey, ILogger<TonWalletSigner> logger)
    {
        Identity = identity;
        _wallet = wallet;
        _secretKey = secretKey;
        _logger = logger;
    }

    public WalletLocalIdentity Identity { get; }

    public static TonWalletSigner Create(AppOptions options, ILogger<TonWalletSigner> logger)
    {
        if (options.MnemonicProblem is not null)
            return Disabled(options.MnemonicProblem, logger);

        if (string.IsNullOrWhiteSpace(options.MnemonicPhrase))
        {
            const string reason = "Signing mnemonic is not configured.";
            logger.LogInformation("Payout signing is disabled: {Reason}", reason);
            return Disabled(reason, logger);
        }

        try
        {
            var words = options.MnemonicPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!Mnemonic.Validate(words))
                return Disabled("TON_MNEMONIC is not a valid TON phrase.", logger);

            var walletId = SerializedWalletId(options.IsTestnet);
            if (!options.IsTestnet && walletId != MainnetWalletId)
                return Disabled("Wallet id encoder does not match mainnet V5R1.", logger);

            var keys = Mnemonic.ToWalletKey(words);
            var contract = WalletV5R1.Create(0, keys.PublicKey, Descriptor(options.IsTestnet));
            var code = contract.Init.Code ?? throw new InvalidOperationException("Wallet code is missing.");
            var codeHash = code.Hash();
            if (!codeHash.AsSpan().SequenceEqual(WalletIdentity.V5R1CodeHash))
                return Disabled("Wallet code does not match V5R1.", logger);

            var derivedAddress = TonAddress.TryParse(contract.Address.ToRawString(), options.IsTestnet, out var derived)
                && string.Equals(derived.Raw, options.Wallet.Raw, StringComparison.OrdinalIgnoreCase);
            if (!derivedAddress)
            {
                logger.LogError(
                    "Derived V5R1 address does not match TON_WALLET_ADDRESS {Configured}.",
                    options.Wallet.NonBounceable);
                return Disabled("Derived wallet address does not match TON_WALLET_ADDRESS.", logger);
            }

            var identity = new WalletLocalIdentity(
                true,
                derived,
                walletId,
                Convert.ToBase64String(codeHash),
                null);
            logger.LogInformation(
                "Wallet V5R1 key matches {Address}. Chain check runs before signing.",
                TonAddress.Shorten(derived.NonBounceable));
            return new TonWalletSigner(identity, contract, keys.SecretKey, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Payout signing is disabled ({Type}).", ex.GetType().Name);
            return Disabled("TON_MNEMONIC could not be used for Wallet V5R1.", logger);
        }
    }

    public SignedTransfer Sign(NativeTransfer transfer)
    {
        if (!Identity.CanSignLocal || _wallet is null || _secretKey is null)
            throw new InvalidOperationException("Signing is disabled.");

        if (transfer.AmountNano <= 0)
            throw new ArgumentOutOfRangeException(nameof(transfer), "Amount must be positive.");

        var unix = transfer.ValidUntilUtc.ToUnixTimeSeconds();
        if (unix is < 1 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(transfer), "validUntil is out of range.");

        try
        {
            var destination = Address.Parse(transfer.Destination.Raw);
            Cell body = string.IsNullOrEmpty(transfer.Comment)
                ? Builder.BeginCell().EndCell()
                : Builder.BeginCell().StoreUint(0, 32).StoreStringTail(transfer.Comment).EndCell();
            var message = new MessageRelaxed(
                new CommonMessageInfoRelaxed.Internal(
                    true,
                    transfer.Bounce,
                    false,
                    null!,
                    destination,
                    new CurrencyCollection(new BigInteger(transfer.AmountNano)),
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    0),
                body,
                null!);
            var signedBody = _wallet.CreateTransfer(
                transfer.Seqno,
                _secretKey,
                new List<MessageRelaxed> { message },
                SendMode.SendPayFwdFeesSeparately | SendMode.SendIgnoreErrors,
                (int)unix,
                "external");
            var external = new Message(
                new CommonMessageInfo.ExternalIn(null!, _wallet.Address, BigInteger.Zero),
                signedBody,
                transfer.Seqno == 0 ? _wallet.Init : null!);
            var cell = Builder.BeginCell();
            external.Store(cell);
            var root = cell.EndCell();
            var hash = root.Hash();
            var boc = root.ToBoc(false, true);
            return new SignedTransfer(
                transfer.Seqno,
                transfer.ValidUntilUtc,
                Convert.ToHexString(hash).ToLowerInvariant(),
                Convert.ToBase64String(hash),
                Convert.ToBase64String(boc));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            _logger.LogError("Signing failed ({Type}).", ex.GetType().Name);
            throw new InvalidOperationException("Couldn't sign the transfer.");
        }
    }

    public override string ToString()
    {
        return "TonWalletSigner";
    }

    internal static int SerializedWalletId(bool testnet)
    {
        var builder = Builder.BeginCell();
        WalletV5R1WalletIdHelper.StoreWalletIdV5R1(Descriptor(testnet))(builder);
        return checked((int)builder.EndCell().AsSlice().LoadInt(32));
    }

    private static WalletIdV5R1 Descriptor(bool testnet)
    {
        return new WalletIdV5R1(testnet ? -3 : -239, new WalletIdV5R1ClientContext("v5r1", 0, 0));
    }

    private static TonWalletSigner Disabled(string reason, ILogger<TonWalletSigner> logger)
    {
        if (!string.Equals(reason, "Signing mnemonic is not configured.", StringComparison.Ordinal))
            logger.LogWarning("Payout signing is disabled: {Reason}", reason);

        return new TonWalletSigner(new WalletLocalIdentity(false, null, 0, null, reason), null, null, logger);
    }
}
