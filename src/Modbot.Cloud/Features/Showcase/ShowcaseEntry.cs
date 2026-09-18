using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Modbot.Cloud.Features.Showcase;

/// <summary>The two kinds of entry the showcase holds.</summary>
/// <remarks>
/// Text, so adding a third kind later never renumbers the two already written.
/// </remarks>
public static class ShowcaseKinds
{
    public const string Sponsor = "sponsor";

    public const string EarlyAdopter = "early-adopter";

    public static IReadOnlyList<string> All { get; } = [Sponsor, EarlyAdopter];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// Somebody the project wants to thank: a sponsor, or a group that used Modbot early.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One table for both kinds</strong>, because they are the same row with a different word on
/// it: a name, a link, a picture, and — when the entry is a VRChat group — the group's id and its two
/// images, so Modbot can show it as a clickable link to that group. Two tables would be the same
/// columns twice, two sets of endpoints and two admin screens, to say "sponsor" instead of "early
/// adopter".
/// </para>
/// <para>
/// <strong>Public.</strong> Every Modbot in the world reads these and shows them on its Credits
/// page, so nothing here is private and nothing is a secret. A Cloud administrator types the rows.
/// </para>
/// <para>
/// Contributors are <em>not</em> in this table. They come from GitHub, which already knows who they
/// are; keeping a second list by hand would only go out of date. See <see cref="GitHubContributors"/>.
/// </para>
/// </remarks>
public sealed class ShowcaseEntry
{
    public const int MaxNameLength = 128;
    public const int MaxUrlLength = 2048;
    public const int MaxKindLength = 16;
    public const int MaxGroupIdLength = 128;

    public Guid Id { get; set; }

    /// <summary><see cref="ShowcaseKinds"/>.</summary>
    public string Kind { get; set; } = ShowcaseKinds.Sponsor;

    /// <summary>What to call them.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where clicking the name goes. Empty when there is nowhere to go.</summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>Their picture. Empty when there is none.</summary>
    public string ImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Their VRChat group, when they have one. Opaque; never checked for shape (foundation 3.1.1).
    /// Modbot turns it into a link to <c>vrchat.com/home/group/…</c>.
    /// </summary>
    public string? VRChatGroupId { get; set; }

    /// <summary>The group's icon.</summary>
    public string? GroupImageUrl { get; set; }

    /// <summary>The group's banner.</summary>
    public string? GroupBannerUrl { get; set; }

    /// <summary>
    /// The copy of <see cref="ImageUrl"/> Cloud keeps and serves, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The three typed-in addresses above are never changed by Cloud: they are what an administrator
    /// entered, and what Cloud fetches from again on the next save. These three are what readers are
    /// actually given. See <see cref="ShowcasePicture"/>.
    /// </remarks>
    public Guid? SavedImageId { get; set; }

    /// <summary>The copy of <see cref="GroupImageUrl"/> Cloud keeps and serves.</summary>
    public Guid? SavedGroupImageId { get; set; }

    /// <summary>The copy of <see cref="GroupBannerUrl"/> Cloud keeps and serves.</summary>
    public Guid? SavedGroupBannerId { get; set; }

    /// <summary>Where it sits in the list. Lowest first, then by name.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

internal sealed class ShowcaseEntryConfiguration : IEntityTypeConfiguration<ShowcaseEntry>
{
    public void Configure(EntityTypeBuilder<ShowcaseEntry> entity)
    {
        entity.ToTable("showcase_entry");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedNever();

        entity.Property(e => e.Kind).HasMaxLength(ShowcaseEntry.MaxKindLength);
        entity.Property(e => e.Name).HasMaxLength(ShowcaseEntry.MaxNameLength);
        entity.Property(e => e.Link).HasMaxLength(ShowcaseEntry.MaxUrlLength);
        entity.Property(e => e.ImageUrl).HasMaxLength(ShowcaseEntry.MaxUrlLength);
        entity.Property(e => e.VRChatGroupId).HasMaxLength(ShowcaseEntry.MaxGroupIdLength);
        entity.Property(e => e.GroupImageUrl).HasMaxLength(ShowcaseEntry.MaxUrlLength);
        entity.Property(e => e.GroupBannerUrl).HasMaxLength(ShowcaseEntry.MaxUrlLength);

        // The only question asked of it: one kind's list, in order.
        entity.HasIndex(e => new { e.Kind, e.SortOrder }).HasDatabaseName("ix_showcase_entry_kind_order");
    }
}
