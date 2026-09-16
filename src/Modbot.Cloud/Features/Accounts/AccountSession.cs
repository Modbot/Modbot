using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// A signed-in account's browser. The table is <c>account_session</c>.
/// </summary>
/// <remarks>
/// Only a hash of the token is stored, so reading the table does not let anyone sign in. Signing out
/// deletes the row; changing or resetting a password deletes every row for that account.
/// </remarks>
public sealed class AccountSession
{
    /// <summary>Lower-case hex SHA-256 of the cookie's value.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid AccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class AccountSessionConfiguration : IEntityTypeConfiguration<AccountSession>
{
    public void Configure(EntityTypeBuilder<AccountSession> entity)
    {
        entity.ToTable("account_session");
        entity.HasKey(s => s.TokenHash);
        entity.Property(s => s.TokenHash).HasMaxLength(64);
        entity.HasIndex(s => s.ExpiresAt);
        entity.HasIndex(s => s.AccountId);

        // Deleting an account signs out every browser it was signed in on.
        entity.HasOne<Account>()
            .WithMany()
            .HasForeignKey(s => s.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
