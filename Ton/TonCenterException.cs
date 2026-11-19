namespace GramWalletService.Ton;

internal enum TonCenterErrorKind
{
    Unavailable,
    Timeout,
    RateLimited,
    InvalidResponse,
    Rejected,
    Duplicate
}

internal sealed class TonCenterException : Exception
{
    public TonCenterException(TonCenterErrorKind kind, string message, int? statusCode = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public TonCenterErrorKind Kind { get; }

    public int? StatusCode { get; }
}
