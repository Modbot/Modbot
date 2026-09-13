using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>One group role, as Modbot records it.</summary>
/// <remarks>
/// Recorded because a role id is not a role name. <c>RoleGranted</c> and <c>RoleRevoked</c> carry
/// whatever VRChat put in the audit entry, which is an id; without a record of what that id meant
/// at the time, a moderation timeline reads "granted grol_9f3c…" forever. Roles are also renamed,
/// so the answer has to be historical rather than a lookup against today's names.
/// </remarks>
public sealed record GroupRoleSnapshot(
    string Id,
    string? Name,
    string? Description,
    int Order,
    bool IsManagementRole,
    bool IsSelfAssignable,
    bool IsAddedOnJoin,
    bool IsDefault,
    IReadOnlyList<string> Permissions)
{
    /// <summary>
    /// Value equality, including the permission list.
    /// </summary>
    /// <remarks>
    /// Written out rather than left to the compiler, because a record's generated
    /// <c>Equals</c> compares <see cref="Permissions"/> <em>by reference</em> — so two roles
    /// freshly deserialised from identical JSON would compare unequal, every poll would look like
    /// a change, and the producer would write the noise it exists to avoid. The failure would be
    /// silent and would look like a very busy group.
    /// </remarks>
    public bool Equals(GroupRoleSnapshot? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Description, other.Description, StringComparison.Ordinal)
        && Order == other.Order
        && IsManagementRole == other.IsManagementRole
        && IsSelfAssignable == other.IsSelfAssignable
        && IsAddedOnJoin == other.IsAddedOnJoin
        && IsDefault == other.IsDefault
        && Permissions.SequenceEqual(other.Permissions, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Id, Name, Description, Order, IsManagementRole, IsSelfAssignable, IsAddedOnJoin, IsDefault);
}

/// <summary>
/// The group metadata Modbot watches for change.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a subset. Banner and icon urls, gallery ids and the member-count-synced-at
/// timestamp all change on their own schedule and mean nothing to a moderator, and including them
/// would make every poll look like a change -- which is the failure this whole type exists to
/// avoid.
/// </para>
/// <para>
/// <strong>No banned-member count.</strong> VRChat's group object does not carry one; the only
/// way to it is paging <c>groups.bans</c>, which is a separate sweep with its own budget. Guessing
/// at it, or deriving it from recorded bans, would be inventing a number.
/// </para>
/// </remarks>
public sealed record GroupInfoSnapshot(
    string? Name,
    string? ShortCode,
    string? Discriminator,
    string? Description,
    string? Rules,
    string? OwnerId,
    string? JoinState,
    string? Privacy,
    bool IsVerified,
    int MemberCount,
    int OnlineMemberCount,
    IReadOnlyList<GroupRoleSnapshot> Roles)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static GroupInfoSnapshot From(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return new GroupInfoSnapshot(
            group.Name,
            group.ShortCode,
            group.Discriminator,
            group.Description,
            group.Rules,
            group.OwnerId,
            group.JoinState?.ToString(),
            group.Privacy?.ToString(),
            group.IsVerified,
            group.MemberCount,
            group.OnlineMemberCount,
            (group.Roles ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                .Select(r => new GroupRoleSnapshot(
                    r.Id,
                    r.Name,
                    r.Description,
                    r.Order,
                    r.IsManagementRole,
                    r.IsSelfAssignable,
                    r.IsAddedOnJoin,
                    r.DefaultRole,
                    // Sorted for the same reason the roles are: VRChat handing back the same
                    // permissions in a different order is not a change to the group.
                    (r.Permissions ?? [])
                        .Select(p => p.ToString())
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList()))
                // Ordered so that VRChat returning the same roles in a different order is not
                // mistaken for a change. Ordering by id rather than by `order` because `order` is
                // itself one of the things being watched.
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .ToList());
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads back a stored snapshot, or nothing if it cannot be read.
    /// </summary>
    /// <remarks>
    /// A snapshot that will not parse -- written by an older shape of this type, say -- is treated
    /// as "no previous snapshot", which produces one baseline fact and carries on. Throwing would
    /// stop group-info sync permanently over a field nobody has looked at in a year.
    /// </remarks>
    public static GroupInfoSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<GroupInfoSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The names of the fields that differ from <paramref name="previous"/>.
    /// </summary>
    /// <remarks>
    /// Empty means the poll saw exactly what was left behind, and <strong>nothing is written</strong>.
    /// A group-info fact per poll would be 288 identical rows a day saying nothing happened, which
    /// is the avatar-line failure from the log research (§4.0) in another costume: it inflates the
    /// count of a thing by an order of magnitude and corrupts every "how often does this change"
    /// question, silently.
    /// </remarks>
    public IReadOnlyList<string> DifferencesFrom(GroupInfoSnapshot? previous)
    {
        if (previous is null)
            return [];

        var changed = new List<string>();

        void Compare<T>(string field, T mine, T theirs)
        {
            if (!EqualityComparer<T>.Default.Equals(mine, theirs))
                changed.Add(field);
        }

        Compare(nameof(Name), Name, previous.Name);
        Compare(nameof(ShortCode), ShortCode, previous.ShortCode);
        Compare(nameof(Discriminator), Discriminator, previous.Discriminator);
        Compare(nameof(Description), Description, previous.Description);
        Compare(nameof(Rules), Rules, previous.Rules);
        Compare(nameof(OwnerId), OwnerId, previous.OwnerId);
        Compare(nameof(JoinState), JoinState, previous.JoinState);
        Compare(nameof(Privacy), Privacy, previous.Privacy);
        Compare(nameof(IsVerified), IsVerified, previous.IsVerified);
        Compare(nameof(MemberCount), MemberCount, previous.MemberCount);
        Compare(nameof(OnlineMemberCount), OnlineMemberCount, previous.OnlineMemberCount);

        if (!Roles.SequenceEqual(previous.Roles))
            changed.Add(nameof(Roles));

        return changed;
    }

    /// <summary>
    /// The payload of a change fact: what changed, and what it changed from and to.
    /// </summary>
    /// <remarks>
    /// Only the changed fields, not the whole snapshot. A group whose member count moves every
    /// five minutes would otherwise carry its full role list into the fact log 288 times a day
    /// to record one integer.
    /// </remarks>
    public JsonObject ChangePayload(GroupInfoSnapshot previous, IReadOnlyList<string> changed)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(changed);

        var before = JsonSerializer.SerializeToNode(previous, Json)!.AsObject();
        var after = JsonSerializer.SerializeToNode(this, Json)!.AsObject();

        var fields = new JsonObject();

        foreach (var field in changed)
        {
            fields[field] = new JsonObject
            {
                ["old"] = before.TryGetPropertyValue(field, out var oldValue) ? oldValue?.DeepClone() : null,
                ["new"] = after.TryGetPropertyValue(field, out var newValue) ? newValue?.DeepClone() : null,
            };
        }

        return new JsonObject { ["changed"] = fields };
    }

    /// <summary>
    /// The payload of the first fact ever recorded for this group: the whole snapshot.
    /// </summary>
    /// <remarks>
    /// This is the baseline the member series is counted forward from. <c>members.net</c> is the
    /// net of recorded joins and leaves, so a group that installs Modbot with 40,000 members would
    /// otherwise see its own headcount start at zero and climb -- the rollup code says as much,
    /// and says the baseline is a sync's job.
    /// </remarks>
    public JsonObject BaselinePayload() =>
        new() { ["baseline"] = JsonSerializer.SerializeToNode(this, Json) };
}
