namespace GramWalletService.Persistence;

public sealed class PayoutTrackedHashEntity
{
    public Guid PayoutId { get; set; }

    public int Ordinal { get; set; }

    public string Hash { get; set; } = "";

    public PayoutEntity Payout { get; set; } = null!;
}
