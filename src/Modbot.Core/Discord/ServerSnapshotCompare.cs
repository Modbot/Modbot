using System.Globalization;

namespace Modbot.Core.Discord;

/// <summary>One role or channel that is different between two snapshots, and how.</summary>
/// <param name="Kind"><see cref="SnapshotChangeKinds.Role"/> or <see cref="SnapshotChangeKinds.Channel"/>.</param>
/// <param name="What"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
/// <param name="Details">One plain sentence per difference. Empty for an added or removed one.</param>
public sealed record SnapshotChange(string Kind, string Id, string Name, string What, IReadOnlyList<string> Details);

public static class SnapshotChangeKinds
{
    public const string Role = "role";
    public const string Channel = "channel";
}

/// <summary>What a restore would change, shown to staff before it runs and checked again when it does.</summary>
/// <param name="Lines">What would change, one sentence each.</param>
/// <param name="Problem">Why it cannot run at all, or null.</param>
/// <param name="Fingerprint">Stands for exactly these changes; the restore is refused if the server no longer gives the same.</param>
public sealed record SnapshotRestorePlan(
    string Kind,
    string Id,
    string Name,
    IReadOnlyList<string> Lines,
    string? Problem,
    string Fingerprint,
    long? RolePermissions,
    IReadOnlyList<SnapshotOverwriteSet> Set,
    IReadOnlyList<string> Remove,
    bool MissingPermission = false)
{
    /// <summary>
    /// Nothing to put back. A line can still be there for a role that is gone, which is left alone, so for a
    /// channel this counts the changes to send, not the lines.
    /// </summary>
    public bool NothingToChange => Problem is null && (RolePermissions is null ? Set.Count + Remove.Count == 0 : Lines.Count == 0);
}

/// <summary>
/// Compares two snapshots, and works out what putting one role's or one channel's permissions back
/// from a snapshot would change in the server as it is now.
/// </summary>
public static class ServerSnapshotCompare
{
    public static IReadOnlyList<SnapshotChange> Compare(ServerSnapshotContent from, ServerSnapshotContent to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var changes = new List<SnapshotChange>();
        var roleName = RoleNames(from, to);

        var fromRoles = from.Roles.ToDictionary(r => r.Id);
        var toRoles = to.Roles.ToDictionary(r => r.Id);

        foreach (var role in to.Roles.OrderByDescending(r => r.Position))
        {
            if (!fromRoles.TryGetValue(role.Id, out var before))
                changes.Add(new SnapshotChange(SnapshotChangeKinds.Role, role.Id, role.Name, "added", []));
            else if (RoleDifferences(before, role) is { Count: > 0 } details)
                changes.Add(new SnapshotChange(SnapshotChangeKinds.Role, role.Id, role.Name, "changed", details));
        }

        foreach (var role in from.Roles.Where(r => !toRoles.ContainsKey(r.Id)).OrderByDescending(r => r.Position))
            changes.Add(new SnapshotChange(SnapshotChangeKinds.Role, role.Id, role.Name, "removed", []));

        var fromChannels = from.Channels.ToDictionary(c => c.Id);
        var toChannels = to.Channels.ToDictionary(c => c.Id);

        foreach (var channel in to.Channels.OrderBy(c => c.Position))
        {
            if (!fromChannels.TryGetValue(channel.Id, out var before))
                changes.Add(new SnapshotChange(SnapshotChangeKinds.Channel, channel.Id, channel.Name, "added", []));
            else if (ChannelDifferences(before, channel, roleName, toChannels, fromChannels) is { Count: > 0 } details)
                changes.Add(new SnapshotChange(SnapshotChangeKinds.Channel, channel.Id, channel.Name, "changed", details));
        }

        foreach (var channel in from.Channels.Where(c => !toChannels.ContainsKey(c.Id)).OrderBy(c => c.Position))
            changes.Add(new SnapshotChange(SnapshotChangeKinds.Channel, channel.Id, channel.Name, "removed", []));

        return changes;
    }

    private static List<string> RoleDifferences(SnapshotRole before, SnapshotRole after)
    {
        var details = new List<string>();

        if (before.Name != after.Name)
            details.Add($"Name: {before.Name} to {after.Name}");

        if (before.Color != after.Color)
            details.Add($"Colour: {Colour(before.Color)} to {Colour(after.Color)}");

        if (before.Position != after.Position)
            details.Add($"Position in the list: {before.Position} to {after.Position}");

        if (before.Hoisted != after.Hoisted)
            details.Add($"Shown apart in the member list: {YesNo(before.Hoisted)} to {YesNo(after.Hoisted)}");

        if (before.Mentionable != after.Mentionable)
            details.Add($"Anyone can mention it: {YesNo(before.Mentionable)} to {YesNo(after.Mentionable)}");

        details.AddRange(PermissionLines("Permissions", "gained", "lost", before.Permissions, after.Permissions));
        return details;
    }

    private static List<string> ChannelDifferences(
        SnapshotChannel before,
        SnapshotChannel after,
        Func<string, string> roleName,
        Dictionary<string, SnapshotChannel> toChannels,
        Dictionary<string, SnapshotChannel> fromChannels)
    {
        var details = new List<string>();

        if (before.Name != after.Name)
            details.Add($"Name: {before.Name} to {after.Name}");

        if (before.CategoryId != after.CategoryId)
        {
            details.Add($"Category: {CategoryName(before.CategoryId, fromChannels, toChannels)} to "
                + CategoryName(after.CategoryId, toChannels, fromChannels));
        }

        if (before.Position != after.Position)
            details.Add($"Position in the list: {before.Position} to {after.Position}");

        if (before.Nsfw != after.Nsfw)
            details.Add($"Age-restricted: {YesNo(before.Nsfw)} to {YesNo(after.Nsfw)}");

        var was = before.Overwrites.ToDictionary(o => o.RoleId);
        var now = after.Overwrites.ToDictionary(o => o.RoleId);

        foreach (var overwrite in after.Overwrites)
        {
            var name = roleName(overwrite.RoleId);

            if (!was.TryGetValue(overwrite.RoleId, out var old))
            {
                details.Add($"{name}: permissions set for this role ({Describe(overwrite.Allow, overwrite.Deny)})");
                continue;
            }

            details.AddRange(PermissionLines($"{name}:", "now allowed", "no longer allowed", old.Allow, overwrite.Allow));
            details.AddRange(PermissionLines($"{name}:", "now denied", "no longer denied", old.Deny, overwrite.Deny));
        }

        foreach (var overwrite in before.Overwrites.Where(o => !now.ContainsKey(o.RoleId)))
            details.Add($"{roleName(overwrite.RoleId)}: permissions for this role removed");

        return details;
    }

    /// <summary>
    /// Says what a set of permissions gained and lost: "Permissions gained: A, B" and "Permissions lost: C",
    /// each only when there is something to say.
    /// </summary>
    private static IEnumerable<string> PermissionLines(string label, string gainedWord, string lostWord, long before, long after)
    {
        var gained = after & ~before;
        var lost = before & ~after;

        if (gained != 0)
            yield return $"{label} {gainedWord}: {DiscordPermissionNames.Line(gained)}";

        if (lost != 0)
            yield return $"{label} {lostWord}: {DiscordPermissionNames.Line(lost)}";
    }

    private static string Describe(long allow, long deny)
    {
        var parts = new List<string>();
        if (allow != 0)
            parts.Add($"allows {DiscordPermissionNames.Line(allow)}");

        if (deny != 0)
            parts.Add($"denies {DiscordPermissionNames.Line(deny)}");

        return parts.Count == 0 ? "allows and denies nothing" : string.Join("; ", parts);
    }

    private static string CategoryName(
        string? id, Dictionary<string, SnapshotChannel> first, Dictionary<string, SnapshotChannel> second)
    {
        if (id is null)
            return "none";

        return first.TryGetValue(id, out var c) || second.TryGetValue(id, out c) ? c.Name : id;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Colour(int rgb) => rgb == 0 ? "none" : "#" + rgb.ToString("x6", CultureInfo.InvariantCulture);

    /// <summary>A role's name, from the newer snapshot first; the id when neither knows it.</summary>
    private static Func<string, string> RoleNames(ServerSnapshotContent from, ServerSnapshotContent to)
    {
        var names = new Dictionary<string, string>();

        foreach (var role in from.Roles)
            names[role.Id] = role.Name;

        foreach (var role in to.Roles)
            names[role.Id] = role.Name;

        return id => names.TryGetValue(id, out var name) ? name : id;
    }

    /// <summary>
    /// What putting <paramref name="snapshot"/>'s permissions for one role back would change, against
    /// the server as it is <paramref name="live"/>.
    /// </summary>
    public static SnapshotRestorePlan PlanRole(ServerSnapshotContent snapshot, ServerStructure live, string roleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(live);

        var saved = snapshot.Roles.FirstOrDefault(r => r.Id == roleId);
        if (saved is null)
            return Stop(SnapshotChangeKinds.Role, roleId, roleId, "That role is not in this snapshot.");

        var now = live.Content.Roles.FirstOrDefault(r => r.Id == roleId);
        if (now is null)
            return Stop(SnapshotChangeKinds.Role, roleId, saved.Name, "That role is gone from the server. Modbot does not make roles again.");

        var lines = new List<string>();
        var gained = saved.Permissions & ~now.Permissions;
        var lost = now.Permissions & ~saved.Permissions;

        if (gained != 0)
            lines.Add($"{now.Name} gets: {DiscordPermissionNames.Line(gained)}");

        if (lost != 0)
            lines.Add($"{now.Name} loses: {DiscordPermissionNames.Line(lost)}");

        string? problem = null;

        if (lines.Count > 0)
        {
            if (!live.BotCanManageRoles)
                problem = "The bot needs Manage Roles.";
            else if (!now.Everyone && now.Position >= live.BotHighestRolePosition)
                problem = "The bot can only change roles below its own highest role.";
            else if ((live.BotPermissions & (1L << 3)) == 0 && (gained & ~live.BotPermissions) != 0)
                problem = $"The bot cannot give what it does not have itself: {DiscordPermissionNames.Line(gained & ~live.BotPermissions)}.";
        }

        // Every problem a role can have here is something the bot lacks.

        return new SnapshotRestorePlan(
            SnapshotChangeKinds.Role,
            roleId,
            now.Name,
            lines,
            problem,
            Fingerprint(SnapshotChangeKinds.Role, roleId, now.Permissions.ToString(CultureInfo.InvariantCulture), saved.Permissions.ToString(CultureInfo.InvariantCulture)),
            saved.Permissions,
            [],
            [],
            MissingPermission: problem is not null);
    }

    /// <summary>
    /// What putting <paramref name="snapshot"/>'s permissions for one channel back would change. Only the
    /// channel's settings for roles are touched; settings for single members are left as they are.
    /// </summary>
    public static SnapshotRestorePlan PlanChannel(ServerSnapshotContent snapshot, ServerStructure live, string channelId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(live);

        var saved = snapshot.Channels.FirstOrDefault(c => c.Id == channelId);
        if (saved is null)
            return Stop(SnapshotChangeKinds.Channel, channelId, channelId, "That channel is not in this snapshot.");

        var now = live.Content.Channels.FirstOrDefault(c => c.Id == channelId);
        if (now is null)
            return Stop(SnapshotChangeKinds.Channel, channelId, saved.Name, "That channel is gone from the server. Modbot does not make channels again.");

        var roleName = RoleNames(snapshot, live.Content);
        var liveRoleIds = live.Content.Roles.Select(r => r.Id).ToHashSet();
        var nowByRole = now.Overwrites.ToDictionary(o => o.RoleId);
        var savedByRole = saved.Overwrites.ToDictionary(o => o.RoleId);

        var lines = new List<string>();
        var set = new List<SnapshotOverwriteSet>();
        var remove = new List<string>();
        var print = new List<string>();
        var missing = 0L;

        foreach (var overwrite in saved.Overwrites)
        {
            var name = roleName(overwrite.RoleId);

            if (!liveRoleIds.Contains(overwrite.RoleId))
            {
                lines.Add($"{name}: left alone, the role is gone from the server");
                continue;
            }

            nowByRole.TryGetValue(overwrite.RoleId, out var current);
            if (current is not null && current.Allow == overwrite.Allow && current.Deny == overwrite.Deny)
                continue;

            if (current is null)
            {
                lines.Add($"{name}: permissions set again ({Describe(overwrite.Allow, overwrite.Deny)})");
            }
            else
            {
                lines.AddRange(PermissionLines($"{name}:", "will allow", "will stop allowing", current.Allow, overwrite.Allow));
                lines.AddRange(PermissionLines($"{name}:", "will deny", "will stop denying", current.Deny, overwrite.Deny));
            }

            // Discord lets the bot allow or deny in a channel only what it holds itself.
            missing |= ((overwrite.Allow & ~(current?.Allow ?? 0)) | (overwrite.Deny & ~(current?.Deny ?? 0))) & ~live.BotPermissions;

            set.Add(new SnapshotOverwriteSet(overwrite.RoleId, overwrite.Allow, overwrite.Deny));
            print.Add($"{overwrite.RoleId}:{current?.Allow}:{current?.Deny}>{overwrite.Allow}:{overwrite.Deny}");
        }

        foreach (var overwrite in now.Overwrites.Where(o => !savedByRole.ContainsKey(o.RoleId)))
        {
            lines.Add($"{roleName(overwrite.RoleId)}: permissions for this role removed");
            remove.Add(overwrite.RoleId);
            print.Add($"{overwrite.RoleId}:{overwrite.Allow}:{overwrite.Deny}>none");
        }

        var changes = set.Count + remove.Count;
        string? problem = null;

        if (changes > 0)
        {
            if (!live.BotCanManageRoles)
                problem = "The bot needs Manage Roles.";
            else if ((live.BotPermissions & (1L << 3)) == 0 && missing != 0)
                problem = $"The bot cannot give what it does not have itself: {DiscordPermissionNames.Line(missing)}.";
        }

        return new SnapshotRestorePlan(
            SnapshotChangeKinds.Channel,
            channelId,
            now.Name,
            lines,
            problem,
            Fingerprint(SnapshotChangeKinds.Channel, channelId, string.Join('|', print)),
            null,
            set,
            remove,
            MissingPermission: problem is not null);
    }

    private static SnapshotRestorePlan Stop(string kind, string id, string name, string problem)
        => new(kind, id, name, [], problem, string.Empty, null, [], []);

    private static string Fingerprint(params string[] parts)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..32];
}
