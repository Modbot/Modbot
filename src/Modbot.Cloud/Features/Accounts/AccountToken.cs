using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>What a token in an email is for.</summary>
public static class TokenPurpose
{
    /// <summary>Confirms the address an account registered with.</summary>
    public const string VerifyEmail = "verify_email";

    /// <summary>Sets a new password without knowing the old one.</summary>
    public const string ResetPassword = "reset_password";

    /// <summary>Confirms an address an account is moving to.</summary>
    public const string ChangeEmail = "change_email";
}

/// <summary>
/// A one-time token Cloud emailed. The table is <c>account_token</c>.
/// </summary>
/// <remarks>
/// Only a SHA-256 of the token is stored, and using one deletes it. Reading this table gives nobody
/// a way in, and the same token can never be used twice.
/// </remarks>
public sealed class AccountToken
{
    /// <summary>Lower-case hex SHA-256 of the token in the mail.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid AccountId { get; set; }

    /// <summary>One of <see cref="TokenPurpose"/>. A token is only ever accepted for its own purpose.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The address a <see cref="TokenPurpose.ChangeEmail"/> token moves the account to.</summary>
    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class AccountTokenConfiguration : IEntityTypeConfiguration<AccountToken>
{
    public void Configure(EntityTypeBuilder<AccountToken> entity)
    {
        entity.ToTable("account_token");
        entity.HasKey(t => t.TokenHash);
        entity.Property(t => t.TokenHash).HasMaxLength(64);
        entity.Property(t => t.Purpose).HasMaxLength(32);
        entity.Property(t => t.Email).HasMaxLength(Account.MaxEmailLength);
        entity.HasIndex(t => t.ExpiresAt);
        entity.HasIndex(t => new { t.AccountId, t.Purpose });

        entity.HasOne<Account>()
            .WithMany()
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
