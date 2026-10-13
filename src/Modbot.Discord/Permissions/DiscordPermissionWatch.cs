using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Permissions;

/// <summary>A role that gained watched permissions, as the server index found it.</summary>
/// <param name="Gained">The watched permission bits the role gained, or was made with.</param>
/// <param name="Created">The role is new since the server was first read.</param>
public sealed record DiscordRoleGain(string RoleId, string Name, bool Managed, long Gained, bool Created);

/// <summary>
/// Tells staff when a Discord role gains a dangerous permission, or a member -- a bot among them --
/// is given a role that has one (<see cref="DiscordDangerousPermissions"/>), and, when the setting
/// is on, takes it away again.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every gain is a fact</strong> (<see cref="FactType.DiscordRoleGainedPermission"/>,
/// <see cref="FactType.DiscordMemberGainedPermission"/>), so it is in the audit log and a channel
/// route can send it. When a channel is chosen on the permission watch card the same news is also
/// posted there directly.
/// </para>
/// <para>
/// <strong>Removal is off unless somebody turns it on.</strong> A role loses the permissions it
/// gained; a member loses the role, except a role a bot's integration made (Discord does not let
/// those be taken off a bot), whose permissions are taken off the role instead. The server's owner
/// never loses a role. When the bot may not do it, the fact and the post say so and nothing else is
/// tried.
/// </para>
/// <para>
/// <strong>What Modbot did itself is not news:</strong> Modbot's own account, and a role Modbot gave
/// in the last few minutes (<see cref="IDiscordGateway.GrantedByModbot"/>). A member whose roles
/// were never stored, so that nothing can be compared, is skipped.
/// </para>
/// </remarks>
public sealed class DiscordPermissionWatch
{
    public const string Reason = "Modbot: permission watch";

    private const string Kept = "kept";
    private const string Removed = "removed";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ILogger _log;

    public DiscordPermissionWatch(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>Roles that gained permissions, from a read of the server.</summary>
    public async Task RolesGainedAsync(
        IDiscordGateway gateway, string guildId, IReadOnlyList<DiscordRoleGain> gains, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentNullException.ThrowIfNull(gains);

        if (gains.Count == 0)
            return;

        var watch = await ReadSettingsAsync(ct).ConfigureAwait(false);

        foreach (var gain in gains)
        {
            var (action, error) = watch.Removes
                ? await StripAsync(gateway, guildId, gain.RoleId, gain.Gained, ct).ConfigureAwait(false)
                : (Kept, null);

            var permissions = string.Join(", ", DiscordDangerousPermissions.Names(gain.Gained));

            await RecordAsync(
                FactType.DiscordRoleGainedPermission,
                gain.RoleId,
                new JsonObject { ["name"] = gain.Name, ["permissions"] = permissions, ["action"] = action, ["error"] = error },
                ct).ConfigureAwait(false);

            await PostAsync(
                gateway,
                watch,
                $"**{CardText.EscapeText(gain.Name)}** {(gain.Created ? "was made with" : "now has")} {permissions}.",
                [],
                action,
                error,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A member joined, or changed. Compares the roles they hold now with the roles stored for them,
    /// before the recorder replaces those.
    /// </summary>
    /// <param name="joined">They have just joined, so every role they hold is new.</param>
    public async Task MemberChangedAsync(
        IDiscordGateway gateway, string guildId, DiscordMemberSnapshot member, bool joined, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentNullException.ThrowIfNull(member);

        if (member.RoleIds.Count == 0 || member.UserId == gateway.BotUserId)
            return;

        HashSet<string> had;

        if (joined)
        {
            had = [];
        }
        else
        {
            var stored = await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId && m.UserId == member.UserId)
                .Select(m => new { m.Roles, m.UpdatedAt })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // Never listed: there is nothing to compare with.
            if (stored is null || stored.UpdatedAt == default)
                return;

            had = Roles(stored.Roles);
        }

        var added = member.RoleIds
            .Where(r => !had.Contains(r) && !gateway.GrantedByModbot(guildId, member.UserId, r))
            .ToList();

        if (added.Count == 0 || gateway.ReadServer(guildId) is not { } server)
            return;

        var watched = server.Roles
            .Where(r => !r.Everyone && added.Contains(r.Id, StringComparer.Ordinal))
            .Select(r => (Role: r, Carried: DiscordDangerousPermissions.In(r.Permissions ?? 0)))
            .Where(r => r.Carried != 0)
            .ToList();

        if (watched.Count == 0)
            return;

        var settings = await ReadSettingsAsync(ct).ConfigureAwait(false);
        var owner = string.Equals(gateway.GuildOwnerId(guildId), member.UserId, StringComparison.Ordinal);

        foreach (var (role, carried) in watched)
        {
            var action = Kept;
            string? error = null;

            if (settings.Removes && !owner)
            {
                // A bot's own role cannot be taken off the bot, so its permissions come off the role.
                (action, error) = role.Managed
                    ? await StripAsync(gateway, guildId, role.Id, carried, ct).ConfigureAwait(false)
                    : await TakeAsync(gateway, guildId, member.UserId, role.Id, ct).ConfigureAwait(false);
            }

            var permissions = string.Join(", ", DiscordDangerousPermissions.Names(carried));

            await RecordAsync(
                FactType.DiscordMemberGainedPermission,
                member.UserId,
                new JsonObject
                {
                    ["displayName"] = member.DisplayName,
                    ["roleId"] = role.Id,
                    ["roleName"] = role.Name,
                    ["permissions"] = permissions,
                    ["action"] = action,
                    ["error"] = error,
                },
                ct).ConfigureAwait(false);

            await PostAsync(
                gateway,
                settings,
                $"**{CardText.EscapeText(member.DisplayName)}**{(member.IsBot ? " (bot)" : "")} was given **{CardText.EscapeText(role.Name)}**, which has {permissions}.",
                owner ? [new DiscordEmbedField("Note", "Server owner", Inline: true)] : [],
                action,
                error,
                ct).ConfigureAwait(false);
        }
    }

    private static async Task<(string Action, string? Error)> StripAsync(
        IDiscordGateway gateway, string guildId, string roleId, long permissions, CancellationToken ct)
    {
        var outcome = await gateway.RemovePermissionsAsync(guildId, roleId, permissions, Reason, ct).ConfigureAwait(false);
        return outcome.Done ? (Removed, null) : (Kept, outcome.Error);
    }

    private static async Task<(string Action, string? Error)> TakeAsync(
        IDiscordGateway gateway, string guildId, string userId, string roleId, CancellationToken ct)
    {
        var outcome = await gateway.ChangeRoleAsync(guildId, userId, roleId, add: false, Reason, ct).ConfigureAwait(false);
        return outcome.Done ? (Removed, null) : (Kept, outcome.Error);
    }

    private async Task RecordAsync(string type, string subjectId, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = subjectId,
                Source = FactSource.Modbot,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Posts to the chosen staff channel, if there is one. A refusal is logged; the fact is already kept.</summary>
    private async Task PostAsync(
        IDiscordGateway gateway,
        Watch watch,
        string description,
        IReadOnlyList<DiscordEmbedField> extra,
        string action,
        string? error,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(watch.ChannelId))
            return;

        var outcomeText = action == Removed
            ? "Permission removed"
            : error is null
                ? "Left as it was"
                : $"Could not remove: {CardText.Fit(CardText.EscapeText(error), 500)}";

        var style = new CardStyle(watch.PublicAddress, watch.GroupName, BrandIcon.For(watch.PublicAddress));

        var card = new DiscordEmbedContent(
            Title: "Dangerous permission on Discord",
            Description: description,
            Color: error is null ? CardColour.Orange : CardColour.Red,
            Fields: [.. extra, new DiscordEmbedField("What happened", outcomeText, Inline: true)],
            Timestamp: _clock.UtcNow,
            Url: null,
            Footer: style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl);

        var outcome = await gateway.PostAsync(watch.ChannelId, [card], ct).ConfigureAwait(false);
        if (!outcome.Sent)
            _log.Warning("Could not tell the staff channel about a dangerous permission: {Reason}", outcome.Error);
    }

    private async Task<Watch> ReadSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new Watch(
                s.DiscordPermissionWatchChannelId,
                s.DiscordPermissionWatchRemoves,
                s.PublicAddress,
                s.ManagedGroupName))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return settings ?? new Watch(null, false, null, null);
    }

    private static HashSet<string> Roles(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record Watch(string? ChannelId, bool Removes, string? PublicAddress, string? GroupName);
}
