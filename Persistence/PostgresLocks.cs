namespace GramWalletService.Persistence;

internal static class PostgresLocks
{
    public const long Wallet = 8142012026L;

    public const long Settings = 8142012027L;

    public static (int Hi, int Lo) Payout(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return (BitConverter.ToInt32(bytes), BitConverter.ToInt32(bytes[4..]));
    }
}
