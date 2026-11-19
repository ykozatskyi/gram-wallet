namespace GramWalletService.Ton;

internal interface IWalletSigner
{
    WalletLocalIdentity Identity { get; }

    SignedTransfer Sign(NativeTransfer transfer);
}

internal sealed record WalletLocalIdentity(
    bool CanSignLocal,
    TonAddress? DerivedAddress,
    int WalletId,
    string? CodeHashBase64,
    string? Problem);

internal sealed record NativeTransfer(
    TonAddress Destination,
    bool Bounce,
    long AmountNano,
    string? Comment,
    int Seqno,
    DateTimeOffset ValidUntilUtc);

internal sealed record SignedTransfer(
    int Seqno,
    DateTimeOffset ValidUntilUtc,
    string MessageHashHex,
    string MessageHashBase64,
    string BocBase64);
