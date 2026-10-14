using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Modbot.Discord.Gateway;

/// <summary>
/// The server's invites: reading them with their use counts, making them, and hearing when they
/// are made or go (invite tracking). Reading and the two events need Manage Server.
/// </summary>
public sealed partial class DiscordNetGateway
{
    public event Func<string, DiscordInviteSnapshot, Task>? InviteCreated;

    public event Func<string, string, Task>? InviteDeleted;

    private Task OnInviteCreated(SocketInvite invite)
    {
        var handler = InviteCreated;
        if (handler is not null && invite.Guild is { } guild)
        {
            var made = Describe(invite);
            _ = Task.Run(() => Guard(handler(Text(guild.Id), made), "invite created"));
        }

        return Task.CompletedTask;
    }

    private Task OnInviteDeleted(SocketGuildChannel channel, string code)
    {
        var handler = InviteDeleted;
        if (handler is not null)
            _ = Task.Run(() => Guard(handler(Text(channel.Guild.Id), code), "invite deleted"));

        return Task.CompletedTask;
    }

    public async Task<DiscordInviteRead> ReadInvitesAsync(string guildId, CancellationToken ct)
    {
        if (ParseId(guildId) is not { } id)
            return DiscordInviteRead.Failed("That is not a Discord server id.");

        if (_client.GetGuild(id) is not { } guild)
            return DiscordInviteRead.Failed("The bot is not in that server.");

        // The library waits out Discord's own limits; the token ends the wait.
        var options = new RequestOptions { CancelToken = ct };

        try
        {
            var invites = (await guild.GetInvitesAsync(options).ConfigureAwait(false))
                .Select(i => Describe(i))
                .ToList();

            // Discord keeps the vanity link apart from the others. A server without one answers
            // with an error that is not a refusal, so only a refusal stops the read.
            try
            {
                if (await guild.GetVanityInviteAsync(options).ConfigureAwait(false) is { } vanity)
                    invites.Add(Describe(vanity) with { IsVanity = true });
            }
            catch (Exception e) when (e is InvalidOperationException
                || (e is HttpException http && http.HttpCode != HttpStatusCode.Forbidden))
            {
            }

            return new DiscordInviteRead(invites, null, false);
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
        {
            return DiscordInviteRead.Failed("The bot needs Manage Server to read the server's invites.", missingPermission: true);
        }
        catch (HttpException e)
        {
            return DiscordInviteRead.Failed($"Could not read the invites: Discord answered {(int)e.HttpCode}.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DiscordInviteRead.Failed($"Could not read the invites: {e.Message}");
        }
    }

    public async Task<DiscordInviteMade> CreateInviteAsync(string guildId, string channelId, string reason, CancellationToken ct)
    {
        if (ParseId(guildId) is not { } gid || _client.GetGuild(gid) is not { } guild)
            return DiscordInviteMade.Failed("The bot is not in that server.");

        if (ParseId(channelId) is not { } cid || guild.GetChannel(cid) is not INestedChannel channel)
            return DiscordInviteMade.Failed("That is not a channel people can be invited to.");

        try
        {
            // Never expires, no limit on uses, and unique: without that, Discord may hand back an
            // invite that already exists, and two names would share one link.
            var made = await channel
                .CreateInviteAsync(
                    maxAge: 0,
                    maxUses: 0,
                    isTemporary: false,
                    isUnique: true,
                    options: new RequestOptions { AuditLogReason = reason, CancelToken = ct })
                .ConfigureAwait(false);

            return new DiscordInviteMade(Describe(made), null, false);
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
        {
            return DiscordInviteMade.Failed("The bot needs Create Invite in that channel.", missingPermission: true);
        }
        catch (HttpException e)
        {
            return DiscordInviteMade.Failed($"Could not make the invite: Discord answered {(int)e.HttpCode}.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DiscordInviteMade.Failed($"Could not make the invite: {e.Message}");
        }
    }

    private static DiscordInviteSnapshot Describe(IInviteMetadata invite)
        => new(
            invite.Code,
            invite.Uses ?? 0,
            invite.MaxUses ?? 0,
            Text(invite.ChannelId),
            invite.Inviter is { } inviter ? Text(inviter.Id) : null,
            invite.CreatedAt,
            ExpiresAt: ExpiryOf(invite.CreatedAt, invite.MaxAge));

    private static DiscordInviteSnapshot Describe(SocketInvite invite)
        => new(
            invite.Code,
            invite.Uses,
            invite.MaxUses,
            Text(invite.ChannelId),
            invite.Inviter is { } inviter ? Text(inviter.Id) : null,
            invite.CreatedAt,
            ExpiresAt: ExpiryOf(invite.CreatedAt, invite.MaxAge));

    /// <summary>When an invite made at <paramref name="createdAt"/> runs out, or null when it never does or is not known.</summary>
    private static DateTimeOffset? ExpiryOf(DateTimeOffset? createdAt, int? maxAgeSeconds)
        => createdAt is { } created && maxAgeSeconds is > 0 ? created.AddSeconds(maxAgeSeconds.Value) : null;
}
