using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Accounts;

/// <summary>
/// A person who signed up on Modbot Cloud. The table is <c>account</c>.
/// </summary>
/// <remarks>
/// <para>
/// An email address, a password and nothing else: no name, no avatar, no organisation. An account
/// exists so that somebody can claim the Modbot servers they own and see them in one place.
/// </para>
/// <para>
/// <strong>This is not a Modbot sign-in.</strong> It never signs anybody in to a Modbot server and no
/// Modbot server ever checks one (Cloud accounts and registry spec 2.1).
/// </para>
/// </remarks>
public sealed class Account
{
    public const int MaxEmailLength = 254;

    public Guid Id { get; set; }

    /// <summary>The address, folded to lower case. Unique.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>From <c>PasswordHasher&lt;Account&gt;</c>. Never returned by anything.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>When the address was verified, or null while it has not been. Null cannot sign in.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>
    /// The address a change is waiting on, or null. The change lands only when the token sent to that
    /// address is used, so a typo locks nobody out of their account.
    /// </summary>
    public string? PendingEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignedInAt { get; set; }
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> entity)
    {
        entity.ToTable("account");
        entity.HasKey(a => a.Id);
        entity.Property(a => a.Id).ValueGeneratedNever();
        entity.Property(a => a.Email).HasMaxLength(Account.MaxEmailLength);
        entity.Property(a => a.PendingEmail).HasMaxLength(Account.MaxEmailLength);
        entity.Property(a => a.PasswordHash).HasMaxLength(256);

        // Unique, so two registrations of the same address race to one row rather than making two.
        entity.HasIndex(a => a.Email).IsUnique();
        entity.HasIndex(a => a.CreatedAt);
    }
}
