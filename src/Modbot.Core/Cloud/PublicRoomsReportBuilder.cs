using Microsoft.EntityFrameworkCore;
using Modbot.Core.Configuration;
using Modbot.Core.Data;

namespace Modbot.Core.Cloud;

/// <summary>
/// Builds the public rooms report out of what this Modbot already knows.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the sender so that what goes in the report can be tested without a Cloud, and so
/// there is one place to read to answer "what leaves this server". The privacy policy quotes this
/// class's rules.
/// </para>
/// <para>
/// Three things have to be true before anything is built: this server may talk to Cloud at all
/// (<c>MODBOT_CLOUD_DISABLED</c>), the setting is on, and a group is being managed. Any of them
/// missing means null, and the sender sends nothing.
/// </para>
/// </remarks>
public sealed class PublicRoomsReportBuilder(ModbotContext db, ModbotCloudAddress cloud)
{
    /// <summary>The one group access type that means "anyone can join".</summary>
    /// <remarks>
    /// VRChat writes <c>members</c> for a room only the group can join and <c>plus</c> for members
    /// and their friends. Neither is public, and neither is ever reported: a link to one on a web
    /// page hands out an address the group deliberately kept inside. A room whose access type
    /// VRChat did not say is treated the same way — not known to be public is not public.
    /// </remarks>
    public const string PublicAccessType = "public";

    /// <summary>
    /// The report, or null when this server has nothing to report or must not report.
    /// </summary>
    public async Task<PublicRoomsReport?> BuildAsync(CancellationToken ct = default)
    {
        if (cloud.Disabled)
            return null;

        var settings = await db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.SharePublicRooms)
            return null;

        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return null;

        var groupId = settings.ManagedGroupId;

        var open = await db.VRChatInstances
            .AsNoTracking()
            .Where(i => i.ClosedAt == null
                        && i.GroupId == groupId
                        && i.GroupAccessType == PublicAccessType)
            .OrderBy(i => i.OpenedAt)
            .Select(i => new
            {
                i.Location,
                i.WorldId,
                i.Region,
                i.OpenedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var worldIds = open.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();

        var worlds = await db.VRChatWorlds
            .AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.ImageUrl, w.ThumbnailImageUrl })
            .ToDictionaryAsync(w => w.WorldId, w => w, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var rooms = open
            .Select(r =>
            {
                var world = worlds.GetValueOrDefault(r.WorldId);

                return new PublicRoomReport(
                    r.Location,
                    r.WorldId,
                    world?.Name,
                    // The thumbnail first: it is the same picture at a size a page of cards can
                    // load, and the full image is only there for a world that has no thumbnail.
                    Picture(world?.ThumbnailImageUrl) ?? Picture(world?.ImageUrl),
                    InstanceJoinLink.For(r.Location, r.WorldId),
                    r.Region,
                    r.OpenedAt);
            })
            .ToList();

        return new PublicRoomsReport(
            groupId,
            settings.ManagedGroupName,
            Picture(settings.ManagedGroupIconUrl),
            Picture(settings.ManagedGroupBannerUrl),
            rooms);
    }

    /// <summary>
    /// A picture address, or null for anything that is not a plain <c>https</c> URL.
    /// </summary>
    /// <remarks>
    /// These addresses end up in an <c>img src</c> on a public web page. Checking the scheme here,
    /// where the value leaves Modbot, means a stored <c>javascript:</c> or <c>data:</c> — however
    /// it got into the row — is dropped before it can reach anyone's browser.
    /// </remarks>
    public static string? Picture(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed.AbsoluteUri
            : null;
}
