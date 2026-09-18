using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.ServerIndex;

/// <summary>
/// Writes the bot's view of its server -- channels, roles, and what the bot may do with each --
/// into <c>discord_server</c>, <c>discord_channel</c> and <c>discord_role</c>.
/// </summary>
/// <remarks>
/// <para>
/// Settings pick channels and roles from these tables instead of asking for a pasted id, and the
/// picker names the permission the bot is missing in a channel (M5 spec §7). They are in Postgres
/// rather than read live from the gateway so the settings page works while the bot is offline.
/// </para>
/// <para>
/// <strong>Nothing is deleted.</strong> A channel or role gone from Discord is marked removed, so a
/// setting still pointing at it can show what it was instead of a bare number.
/// </para>
/// <para>
/// The bot service calls this one piece of work at a time; it does no locking of its own.
/// </para>
/// </remarks>
public sealed class DiscordServerIndex
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public DiscordServerIndex(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Replaces the stored picture of the server with this one: every channel and role in it is
    /// saved, and every stored one missing from it is marked removed.
    /// </summary>
    public async Task RefreshAsync(DiscordServerSnapshot server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);

        var now = _clock.UtcNow;

        var row = await ServerRowAsync(server.GuildId, create: true, ct).ConfigureAwait(false);
        row!.Name = server.Name;
        row.BotCanViewAuditLog = server.BotCanViewAuditLog;
        row.BotCanManageRoles = server.BotCanManageRoles;
        row.BotCanManageEvents = server.BotCanManageEvents;
        row.BotCanBanMembers = server.BotCanBanMembers;
        row.BotCanRemoveMembers = server.BotCanRemoveMembers;
        row.RefreshedAt = now;
        row.UpdatedAt = now;

        // Ids are unique across Discord, so a row already stored under another server id (the
        // settings were pointed elsewhere and back) is the same channel and is taken over.
        var channelIds = server.Channels.Select(c => c.Id).ToList();
        var channels = await _db.DiscordChannels
            .Where(c => c.GuildId == server.GuildId || channelIds.Contains(c.ChannelId))
            .ToDictionaryAsync(c => c.ChannelId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        foreach (var snapshot in server.Channels)
        {
            if (!channels.TryGetValue(snapshot.Id, out var channel))
            {
                channel = new DiscordChannel { ChannelId = snapshot.Id, FirstSeenAt = now };
                _db.DiscordChannels.Add(channel);
                channels[snapshot.Id] = channel;
            }

            Apply(channel, server.GuildId, snapshot, now);
        }

        var presentChannels = channelIds.ToHashSet(StringComparer.Ordinal);
        foreach (var channel in channels.Values)
        {
            if (channel.GuildId == server.GuildId && channel.RemovedAt is null && !presentChannels.Contains(channel.ChannelId))
            {
                channel.RemovedAt = now;
                channel.UpdatedAt = now;
            }
        }

        var roleIds = server.Roles.Select(r => r.Id).ToList();
        var roles = await _db.DiscordRoles
            .Where(r => r.GuildId == server.GuildId || roleIds.Contains(r.RoleId))
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        foreach (var snapshot in server.Roles)
        {
            if (!roles.TryGetValue(snapshot.Id, out var role))
            {
                role = new DiscordRole { RoleId = snapshot.Id, FirstSeenAt = now };
                _db.DiscordRoles.Add(role);
                roles[snapshot.Id] = role;
            }

            Apply(role, server.GuildId, snapshot, now);
        }

        var presentRoles = roleIds.ToHashSet(StringComparer.Ordinal);
        foreach (var role in roles.Values)
        {
            if (role.GuildId == server.GuildId && role.RemovedAt is null && !presentRoles.Contains(role.RoleId))
            {
                role.RemovedAt = now;
                role.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Saves one channel that was created or changed.</summary>
    public async Task SaveChannelAsync(string guildId, DiscordChannelSnapshot snapshot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = _clock.UtcNow;

        var channel = await _db.DiscordChannels
            .FirstOrDefaultAsync(c => c.ChannelId == snapshot.Id, ct)
            .ConfigureAwait(false);

        if (channel is null)
        {
            channel = new DiscordChannel { ChannelId = snapshot.Id, FirstSeenAt = now };
            _db.DiscordChannels.Add(channel);
        }

        Apply(channel, guildId, snapshot, now);

        if (await ServerRowAsync(guildId, create: false, ct).ConfigureAwait(false) is { } server)
            server.UpdatedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks one channel removed. A channel never stored is nothing to do.</summary>
    public async Task RemoveChannelAsync(string guildId, string channelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var channel = await _db.DiscordChannels
            .FirstOrDefaultAsync(c => c.ChannelId == channelId && c.GuildId == guildId, ct)
            .ConfigureAwait(false);

        if (channel is null || channel.RemovedAt is not null)
            return;

        var now = _clock.UtcNow;
        channel.RemovedAt = now;
        channel.UpdatedAt = now;

        if (await ServerRowAsync(guildId, create: false, ct).ConfigureAwait(false) is { } server)
            server.UpdatedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<DiscordServer?> ServerRowAsync(string guildId, bool create, CancellationToken ct)
    {
        var row = await _db.DiscordServers
            .FirstOrDefaultAsync(s => s.GuildId == guildId, ct)
            .ConfigureAwait(false);

        if (row is null && create)
        {
            row = new DiscordServer { GuildId = guildId };
            _db.DiscordServers.Add(row);
        }

        return row;
    }

    private static void Apply(DiscordChannel row, string guildId, DiscordChannelSnapshot snapshot, DateTimeOffset now)
    {
        var permissions = snapshot.BotPermissions;

        var same = row.GuildId == guildId
            && row.Name == snapshot.Name
            && row.Type == snapshot.Type
            && row.CategoryId == snapshot.CategoryId
            && row.Position == snapshot.Position
            && row.Nsfw == snapshot.Nsfw
            && row.BotCanView == permissions.ViewChannel
            && row.BotCanReadHistory == permissions.ReadMessageHistory
            && row.BotCanSend == permissions.SendMessages
            && row.BotCanEmbedLinks == permissions.EmbedLinks
            && row.BotCanAttachFiles == permissions.AttachFiles
            && row.BotCanManageMessages == permissions.ManageMessages
            && row.RemovedAt is null;

        if (same)
            return;

        row.GuildId = guildId;
        row.Name = snapshot.Name;
        row.Type = snapshot.Type;
        row.CategoryId = snapshot.CategoryId;
        row.Position = snapshot.Position;
        row.Nsfw = snapshot.Nsfw;
        row.BotCanView = permissions.ViewChannel;
        row.BotCanReadHistory = permissions.ReadMessageHistory;
        row.BotCanSend = permissions.SendMessages;
        row.BotCanEmbedLinks = permissions.EmbedLinks;
        row.BotCanAttachFiles = permissions.AttachFiles;
        row.BotCanManageMessages = permissions.ManageMessages;
        row.RemovedAt = null;
        row.UpdatedAt = now;
    }

    private static void Apply(DiscordRole row, string guildId, DiscordRoleSnapshot snapshot, DateTimeOffset now)
    {
        var same = row.GuildId == guildId
            && row.Name == snapshot.Name
            && row.Color == snapshot.Color
            && row.Position == snapshot.Position
            && row.Managed == snapshot.Managed
            && row.Everyone == snapshot.Everyone
            && row.BotCanAssign == snapshot.BotCanAssign
            && row.RemovedAt is null;

        if (same)
            return;

        row.GuildId = guildId;
        row.Name = snapshot.Name;
        row.Color = snapshot.Color;
        row.Position = snapshot.Position;
        row.Managed = snapshot.Managed;
        row.Everyone = snapshot.Everyone;
        row.BotCanAssign = snapshot.BotCanAssign;
        row.RemovedAt = null;
        row.UpdatedAt = now;
    }
}
