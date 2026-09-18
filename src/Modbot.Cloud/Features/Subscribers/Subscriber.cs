using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modbot.Cloud.Features.Accounts;

namespace Modbot.Cloud.Features.Subscribers;

/// <summary>
/// Somebody who asked to hear from Modbot about new features and updates. The table is
/// <c>subscriber</c>.
/// </summary>
/// <remarks>
/// <para>
/// An address, where the person ticked the box, and two times. No name, no account, no link to a
/// server or to a moderation record: a mailing list is a mailing list, and anything else kept beside
/// it would turn one into a profile.
/// </para>
/// <para>
/// One row per address, whatever case it was typed in. Ticking the box again on another Modbot
/// moves <see cref="LastSeenAt"/> and adds nothing.
/// </para>
/// </remarks>
public sealed class Subscriber
{
    public const int MaxSourceLength = 64;

    /// <summary>The address, trimmed and folded to lower case.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Where the person ticked the box, as the caller described it.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// The secret in the unsubscribe link. Random, long-lived, and the whole of what that link
    /// proves: holding it means holding a link sent to that address.
    /// </summary>
    public string UnsubscribeToken { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When they asked to stop, or null while they have not.</summary>
    public DateTimeOffset? UnsubscribedAt { get; set; }
}

internal sealed class SubscriberConfiguration : IEntityTypeConfiguration<Subscriber>
{
    public void Configure(EntityTypeBuilder<Subscriber> entity)
    {
        entity.ToTable("subscriber");
        entity.HasKey(s => s.Email);
        entity.Property(s => s.Email).HasMaxLength(Account.MaxEmailLength);
        entity.Property(s => s.Source).HasMaxLength(Subscriber.MaxSourceLength);
        entity.Property(s => s.UnsubscribeToken).HasMaxLength(64);

        // The unsubscribe link looks a row up by its token alone, so that has to be a seek and the
        // token has to be one person's.
        entity.HasIndex(s => s.UnsubscribeToken).IsUnique();
        entity.HasIndex(s => s.FirstSeenAt);
    }
}
