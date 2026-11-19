using System.Text;
using GramWalletService.Payouts;
using Microsoft.EntityFrameworkCore;

namespace GramWalletService.Persistence;

public sealed class WalletDbContext : DbContext
{
    public WalletDbContext(DbContextOptions<WalletDbContext> options)
        : base(options)
    {
    }

    public DbSet<PayoutEntity> Payouts => Set<PayoutEntity>();

    public DbSet<PayoutTrackedHashEntity> PayoutTrackedHashes => Set<PayoutTrackedHashEntity>();

    public DbSet<PayoutSettingsEntity> PayoutSettings => Set<PayoutSettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var payout = modelBuilder.Entity<PayoutEntity>();
        payout.ToTable("payouts", table =>
            table.HasCheckConstraint("ck_payouts_amount_positive", "amount_nano > 0"));
        payout.HasKey(entity => entity.Id);
        payout.Property(entity => entity.Id).ValueGeneratedNever();
        payout.Property(entity => entity.RequestId).HasMaxLength(36).IsRequired();
        payout.Property(entity => entity.DestinationRaw).HasMaxLength(128).IsRequired();
        payout.Property(entity => entity.DestinationFriendly).HasMaxLength(128).IsRequired();
        payout.Property(entity => entity.Comment);
        payout.Property(entity => entity.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        payout.Property(entity => entity.ExternalMessageHash).HasMaxLength(256);
        payout.Property(entity => entity.TransactionHash).HasMaxLength(256);
        payout.Property(entity => entity.SignedBoc);
        payout.Property(entity => entity.LastSend).HasConversion<string>().HasMaxLength(32).IsRequired();
        payout.Property(entity => entity.LastError);
        payout.HasIndex(entity => entity.RequestId)
            .IsUnique()
            .HasDatabaseName("ux_payouts_request_id");
        payout.HasIndex(entity => new { entity.Status, entity.CreatedAtUtc });
        payout.HasIndex(entity => entity.CreatedAtUtc);
        payout.HasMany(entity => entity.Hashes)
            .WithOne(hash => hash.Payout)
            .HasForeignKey(hash => hash.PayoutId)
            .OnDelete(DeleteBehavior.Cascade);

        var hash = modelBuilder.Entity<PayoutTrackedHashEntity>();
        hash.ToTable("payout_tracked_hashes");
        hash.HasKey(entity => new { entity.PayoutId, entity.Ordinal });
        hash.Property(entity => entity.Hash).HasMaxLength(256).IsRequired();

        var settings = modelBuilder.Entity<PayoutSettingsEntity>();
        settings.ToTable("payout_settings");
        settings.HasKey(entity => entity.Id);
        settings.Property(entity => entity.Id).ValueGeneratedNever();

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnake(property.Name));
        }
    }

    private static string ToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');

                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
