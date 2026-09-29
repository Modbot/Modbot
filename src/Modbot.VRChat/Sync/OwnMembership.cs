using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Modbot's own membership of the group, as <c>myMember</c> in the group-info answer states it.
/// </summary>
/// <remarks>
/// <para>
/// The member list never includes the account asking for it, so without this the Members list is
/// one short of the count VRChat shows for the group: the missing one is Modbot's own account.
/// The group-info poll already carries <c>myMember</c>, so the row costs no request of its own.
/// </para>
/// <para>
/// Read from the raw answer rather than the SDK's typed object, for the reason
/// <see cref="VRChatGroupPermissions.AccountFrom"/> gives: the typed object cannot hold a value its
/// enum does not name, and a status is kept as VRChat's own word.
/// </para>
/// </remarks>
internal sealed record OwnMembership(
    string UserId,
    string? MembershipId,
    string Roles,
    DateTimeOffset? JoinedAt,
    string? MembershipStatus,
    string? Visibility,
    bool IsRepresenting,
    string? ManagerNotes,
    string Raw)
{
    /// <summary>The membership in a group-info answer. Null when there is no <c>myMember</c> or it names no user.</summary>
    public static OwnMembership? From(string? groupJson)
    {
        if (string.IsNullOrWhiteSpace(groupJson))
            return null;

        try
        {
            if (JsonNode.Parse(groupJson) is not JsonObject group
                || !group.TryGetPropertyValue("myMember", out var node)
                || node is not JsonObject me
                || Text(me, "userId") is not { } userId)
            {
                return null;
            }

            var roleIds = me.TryGetPropertyValue("roleIds", out var roles) && roles is JsonArray list
                ? list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()
                : [];

            var joinedAt = Text(me, "joinedAt") is { } joined
                && DateTimeOffset.TryParse(
                    joined, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : (DateTimeOffset?)null;

            var representing = me.TryGetPropertyValue("isRepresenting", out var rep)
                && rep is JsonValue r
                && r.TryGetValue<bool>(out var yes)
                && yes;

            return new OwnMembership(
                userId,
                Text(me, "id"),
                GroupMemberSync.RolesJson(roleIds),
                joinedAt,
                Text(me, "membershipStatus"),
                Text(me, "visibility"),
                representing,
                Text(me, "managerNotes"),
                me.ToJsonString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var value)
        && value is JsonValue v
        && v.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
