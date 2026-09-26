using System.Text.Json;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>
/// What Modbot keeps out of a VRChat world page, and how it gets onto the row.
/// </summary>
/// <remarks>
/// <para>
/// A world page carries far more than this -- visit counts, heat, popularity, favourites, the
/// whole Unity package list. None of it is kept. Modbot needs to be able to write a world's name
/// on a screen and say who made it; the rest is VRChat's own store-front data, it changes
/// constantly, and storing it would mean either re-reading every world forever or showing figures
/// that quietly went stale months ago.
/// </para>
/// <para>
/// One thing is taken from the Unity package list: which platforms have a build. The game's own
/// instance card shows them as PC, Android and iOS badges, and a moderator matching an instance to
/// the game looks for them. Unlike the counts, they change only when the creator uploads, so they
/// do not go stale the way the rest would (2026-09-26).
/// </para>
/// <para>
/// Capacity is kept and is <strong>never</strong> treated as a limit Modbot enforces: exemptions
/// raise real capacity above what the page says (foundation section 3.1).
/// </para>
/// </remarks>
public sealed record WorldSnapshot(
    string WorldId,
    string? Name,
    string? Description,
    string? AuthorId,
    string? AuthorName,
    string? ImageUrl,
    string? ThumbnailImageUrl,
    int? Capacity,
    int? RecommendedCapacity,
    string? Tags,
    string? Platforms,
    string? ReleaseStatus,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt)
{
    public static WorldSnapshot From(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        return new WorldSnapshot(
            world.Id ?? string.Empty,
            world.Name,
            world.Description,
            world.AuthorId,
            world.AuthorName,
            world.ImageUrl,
            world.ThumbnailImageUrl,
            world.Capacity,
            world.RecommendedCapacity,
            world.Tags is { Count: > 0 } tags ? JsonSerializer.Serialize(tags) : null,
            PlatformsOf(world),
            world.ReleaseStatus.ToString(),
            AsInstant(world.PublicationDate),
            world.UpdatedAt);
    }

    /// <summary>
    /// Writes the snapshot onto a row and marks it read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A field VRChat did not send does not erase what is already there.</strong> The
    /// world object attached to a group instance is not always as complete as the world's own
    /// page, so a poll that arrives with no description must not wipe a description an earlier
    /// read had. Only the name is allowed through unconditionally, because a world genuinely
    /// renamed to something shorter is a real change worth following.
    /// </para>
    /// </remarks>
    public void ApplyTo(VRChatWorld row, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.Name = Name ?? row.Name;
        row.Description = Description ?? row.Description;
        row.AuthorId = AuthorId ?? row.AuthorId;
        row.AuthorName = AuthorName ?? row.AuthorName;
        row.ImageUrl = ImageUrl ?? row.ImageUrl;
        row.ThumbnailImageUrl = ThumbnailImageUrl ?? row.ThumbnailImageUrl;
        row.Capacity = Capacity ?? row.Capacity;
        row.RecommendedCapacity = RecommendedCapacity ?? row.RecommendedCapacity;
        row.Tags = Tags ?? row.Tags;
        row.Platforms = Platforms ?? row.Platforms;
        row.ReleaseStatus = ReleaseStatus ?? row.ReleaseStatus;
        row.PublishedAt = PublishedAt ?? row.PublishedAt;
        row.UpdatedAt = UpdatedAt ?? row.UpdatedAt;

        row.LastRefreshedAt = readAt;
        row.RefreshError = null;
    }

    /// <summary>
    /// The platforms with a build, once each, in a steady order. Null when the read carried no
    /// builds at all, which is not the same as a world with none.
    /// </summary>
    private static string? PlatformsOf(World world)
    {
        var platforms = (world.UnityPackages ?? [])
            .Select(p => p.Platform)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return platforms.Count > 0 ? JsonSerializer.Serialize(platforms) : null;
    }

    /// <summary>
    /// Reads VRChat's publication date, which is a string and is sometimes the word "none".
    /// </summary>
    /// <remarks>
    /// A world in Labs, or one never published, carries <c>none</c> rather than a date or an empty
    /// string. Parsing it as a date would throw on an ordinary world, so anything that is not a
    /// date is simply no date.
    /// </remarks>
    private static DateTimeOffset? AsInstant(string? published) =>
        DateTimeOffset.TryParse(published, out var parsed) ? parsed : null;
}
