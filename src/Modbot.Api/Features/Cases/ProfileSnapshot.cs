using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Cases;

/// <summary>
/// What was captured: the three rows as JSON, and when VRChat had last been asked.
/// </summary>
/// <param name="Profile">Null when Modbot had never fetched the person's profile.</param>
/// <param name="ProfileRefreshedAt">The row's <c>last_refreshed_at</c> at capture. The profile is only as current as this.</param>
public sealed record CapturedSnapshot(
    string? Profile,
    string? Membership,
    string? BanListEntry,
    DateTimeOffset? ProfileRefreshedAt);

/// <summary>
/// Copies the person's stored rows into a case file, as they stand, without touching them.
/// </summary>
/// <remarks>
/// <para>
/// Evidence design §12: the profile snapshot is structured data, never a picture, so "everyone we
/// banned whose bio mentioned this invite" stays a query. It is taken from what Modbot already
/// holds rather than fetched -- fetching would spend the users lane and could fail or stall, and
/// the write-up must never wait on VRChat (§12.2). The caller asks the profile sync for a fresher
/// copy separately; if one arrives, the moderator can capture again once.
/// </para>
/// <para>
/// <strong>This reads and never writes.</strong> The rows it copies belong to the sweeps and the
/// profile sync; a snapshot that updated them would be a fourth writer with a different idea of
/// what "seen" means. Every query is <c>AsNoTracking</c> so nothing here can be saved by accident.
/// </para>
/// <para>
/// The JSON is the row's own fields, named as the API names them elsewhere, plus the raw object
/// VRChat sent -- as received, not normalised, because a normaliser written today drops the field
/// VRChat adds next year (§12.1). No field is required to be present and no id is validated.
/// </para>
/// </remarks>
public static class ProfileSnapshot
{
    public static async Task<CapturedSnapshot> CaptureAsync(
        ModbotContext db, string userId, string? groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var user = await db.VRChatUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId, ct);

        var member = groupId is null
            ? null
            : await db.GroupMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct);

        var ban = groupId is null
            ? null
            : await db.GroupBans.AsNoTracking()
                .FirstOrDefaultAsync(b => b.GroupId == groupId && b.UserId == userId, ct);

        // A row with no refresh yet holds only the id and when it was seen. That is not a profile,
        // and recording it as one would make "Modbot had nothing" look like "the bio was empty".
        var profile = user is { LastRefreshedAt: not null } or { LastUserReadAt: not null } ? Profile(user) : null;

        return new CapturedSnapshot(
            profile?.ToJsonString(),
            member is null ? null : Membership(member).ToJsonString(),
            ban is null ? null : BanListEntry(ban).ToJsonString(),
            user?.LastRefreshedAt);
    }

    private static JsonObject Profile(VRChatUser u) => new()
    {
        ["userId"] = u.UserId,
        ["displayName"] = u.DisplayName,
        ["bio"] = u.Bio,
        ["status"] = u.Status,
        ["statusDescription"] = u.StatusDescription,
        ["pronouns"] = u.Pronouns,
        ["avatarImageUrl"] = u.CurrentAvatarImageUrl,
        ["avatarThumbnailUrl"] = u.CurrentAvatarThumbnailImageUrl,
        ["profilePictureUrl"] = u.ProfilePictureUrl,
        ["dateJoined"] = u.DateJoined?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["tags"] = Parse(u.Tags) ?? new JsonArray(),
        ["lastPlatform"] = u.LastPlatform,
        ["ageVerificationStatus"] = u.AgeVerificationStatus,
        ["ageVerified"] = u.AgeVerified,
        ["eighteenPlus"] = new JsonObject
        {
            ["verified"] = u.Is18PlusVerified,
            ["since"] = Time(u.Is18PlusVerifiedAt),
            ["source"] = u.Is18PlusVerifiedSource,
        },
        ["firstSeenAt"] = Time(u.FirstSeenAt),
        ["lastSeenAt"] = Time(u.LastSeenAt),
        ["lastRefreshedAt"] = Time(u.LastRefreshedAt),
        ["lastUserReadAt"] = Time(u.LastUserReadAt),
        ["notFoundAt"] = Time(u.NotFoundAt),

        // Both bodies. VRChat answers two different calls about a person and they carry
        // different fields, so a case file that kept only one would be missing half of what
        // Modbot was told (research: vrchat-public-profile-findings.md).
        ["raw"] = Parse(u.RawProfile),
        ["rawPublicProfile"] = Parse(u.RawPublicProfile),
    };

    private static JsonObject Membership(GroupMember m) => new()
    {
        ["isMember"] = m.LeftAt is null,
        ["membershipId"] = m.MembershipId,
        ["roleIds"] = Parse(m.Roles) ?? new JsonArray(),
        ["joinedAt"] = Time(m.JoinedAt),
        ["membershipStatus"] = m.MembershipStatus,
        ["visibility"] = m.Visibility,
        ["isRepresenting"] = m.IsRepresenting,
        ["managerNotes"] = m.ManagerNotes,
        ["firstSeenAt"] = Time(m.FirstSeenAt),
        ["lastSeenAt"] = Time(m.LastSeenAt),
        ["leftAt"] = Time(m.LeftAt),
        ["raw"] = Parse(m.Raw),
    };

    private static JsonObject BanListEntry(GroupBan b) => new()
    {
        ["bannedAt"] = Time(b.BannedAt),
        ["firstSeenAt"] = Time(b.FirstSeenAt),
        ["lastSeenAt"] = Time(b.LastSeenAt),
        ["liftedAt"] = Time(b.LiftedAt),
        ["raw"] = Parse(b.Raw),
    };

    private static string? Time(DateTimeOffset? at)
        => at?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Stored jsonb back into a node. A column that will not parse is kept as its text rather than lost.</summary>
    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}
