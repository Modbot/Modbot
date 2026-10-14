using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Modbot.Core.Discord;

namespace Modbot.Discord.Gateway;

/// <summary>
/// The server's roles and channels with their permissions, read for a snapshot, and one role's or
/// one channel's permissions put back from one. Reading uses the session's own picture of the
/// server; putting back needs Manage Roles.
/// </summary>
public sealed partial class DiscordNetGateway
{
    public ServerStructure? ReadStructure(string guildId)
    {
        if (ParseId(guildId) is not { } id || _client.GetGuild(id) is not { } guild)
            return null;

        var me = guild.CurrentUser;
        var serverWide = me?.GuildPermissions ?? GuildPermissions.None;

        var roles = guild.Roles
            .Select(r => new SnapshotRole(
                Text(r.Id),
                r.Name,
                (int)r.Colors.PrimaryColor.RawValue,
                r.Position,
                (long)r.Permissions.RawValue,
                r.IsHoisted,
                r.IsMentionable,
                r.IsManaged,
                r.IsEveryone))
            .OrderByDescending(r => r.Position)
            .ToList();

        var channels = guild.Channels
            .Select(DescribeForSnapshot)
            .OfType<SnapshotChannel>()
            .OrderBy(c => c.Position)
            .ToList();

        return new ServerStructure(
            new ServerSnapshotContent(ServerSnapshotContent.CurrentVersion, Text(guild.Id), guild.Name, roles, channels),
            serverWide.ManageRoles,
            (long)serverWide.RawValue,
            me?.Roles.Where(r => !r.IsEveryone).Select(r => r.Position).DefaultIfEmpty(0).Max() ?? 0);
    }

    public async Task<DiscordEditResult> SetRolePermissionsAsync(
        string guildId, string roleId, long permissions, string reason, CancellationToken ct)
    {
        if (ParseId(guildId) is not { } gid || _client.GetGuild(gid) is not { } guild)
            return DiscordEditResult.Failed("The bot is not in that server.");

        if (ParseId(roleId) is not { } rid || guild.GetRole(rid) is not { } role)
            return DiscordEditResult.Failed("That role is gone from the server.");

        try
        {
            await role
                .ModifyAsync(
                    p => p.Permissions = new GuildPermissions((ulong)permissions),
                    new RequestOptions { AuditLogReason = reason, CancelToken = ct })
                .ConfigureAwait(false);

            return DiscordEditResult.Ok(1);
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
        {
            return DiscordEditResult.Failed(
                "Discord refused: the bot needs Manage Roles and a role above this one.", missingPermission: true);
        }
        catch (HttpException e)
        {
            return DiscordEditResult.Failed($"Could not change the role: Discord answered {(int)e.HttpCode}.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DiscordEditResult.Failed($"Could not change the role: {e.Message}");
        }
    }

    public async Task<DiscordEditResult> SetChannelOverwritesAsync(
        string guildId,
        string channelId,
        IReadOnlyList<SnapshotOverwriteSet> set,
        IReadOnlyList<string> removeRoleIds,
        string reason,
        CancellationToken ct)
    {
        if (ParseId(guildId) is not { } gid || _client.GetGuild(gid) is not { } guild)
            return DiscordEditResult.Failed("The bot is not in that server.");

        if (ParseId(channelId) is not { } cid || guild.GetChannel(cid) is not IGuildChannel channel)
            return DiscordEditResult.Failed("That channel is gone from the server.");

        var options = new RequestOptions { AuditLogReason = reason, CancelToken = ct };
        var applied = 0;

        try
        {
            foreach (var change in set)
            {
                if (ParseId(change.RoleId) is not { } rid || guild.GetRole(rid) is not { } role)
                    continue;

                await channel
                    .AddPermissionOverwriteAsync(role, new OverwritePermissions((ulong)change.Allow, (ulong)change.Deny), options)
                    .ConfigureAwait(false);
                applied++;
            }

            foreach (var roleId in removeRoleIds)
            {
                if (ParseId(roleId) is not { } rid || guild.GetRole(rid) is not { } role)
                    continue;

                await channel.RemovePermissionOverwriteAsync(role, options).ConfigureAwait(false);
                applied++;
            }

            return DiscordEditResult.Ok(applied);
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
        {
            return DiscordEditResult.Failed(
                "Discord refused: the bot needs Manage Roles, and can only allow or deny in a channel what it has there itself.",
                applied,
                missingPermission: true);
        }
        catch (HttpException e)
        {
            return DiscordEditResult.Failed($"Could not change the channel: Discord answered {(int)e.HttpCode}.", applied);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DiscordEditResult.Failed($"Could not change the channel: {e.Message}", applied);
        }
    }

    /// <summary>A channel or category as a snapshot keeps it; null for kinds Modbot does not list (threads).</summary>
    private static SnapshotChannel? DescribeForSnapshot(SocketGuildChannel channel)
    {
        if (Describe(channel) is not { } described)
            return null;

        var overwrites = channel.PermissionOverwrites
            .Where(o => o.TargetType == PermissionTarget.Role)
            .Select(o => new SnapshotOverwrite(Text(o.TargetId), (long)o.Permissions.AllowValue, (long)o.Permissions.DenyValue))
            .OrderBy(o => o.RoleId, StringComparer.Ordinal)
            .ToList();

        return new SnapshotChannel(
            described.Id, described.Name, described.Type, described.CategoryId, described.Position, described.Nsfw, overwrites);
    }
}
