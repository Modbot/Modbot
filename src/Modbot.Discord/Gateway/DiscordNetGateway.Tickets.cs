using System.Globalization;
using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Modbot.Discord.Gateway;

/// <summary>Member tickets: a private thread for each, made, closed and deleted through the session.</summary>
public sealed partial class DiscordNetGateway
{
    public async Task<DiscordTicketOutcome> OpenTicketThreadAsync(DiscordTicketOpening opening, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opening);

        if (ParseId(opening.MemberId) is not { } member)
            return DiscordTicketOutcome.Failed("That is not a Discord user id.");

        var role = ParseId(opening.StaffRoleId);

        var made = await InChannelAsync(opening.ChannelId, ChannelNeed.TicketOpen, async channel =>
        {
            if (channel is not ITextChannel parent)
            {
                return DiscordPostOutcome.Failed(
                    "Tickets can only be opened from a text channel, not a thread or a forum.", permanent: true);
            }

            // A week without a word archives it; writing in it opens it again. Nobody else may add people.
            var thread = await parent.CreateThreadAsync(
                    opening.ThreadName,
                    ThreadType.PrivateThread,
                    ThreadArchiveDuration.OneWeek,
                    invitable: false,
                    options: Request(ct))
                .ConfigureAwait(false);

            try
            {
                await AddToThreadAsync(thread, parent.GuildId, member, ct).ConfigureAwait(false);

                var userIds = new List<ulong> { member };
                var roleIds = new List<ulong>();
                var mentions = $"<@{Text(member)}>";

                // The @everyone role's id is the server's; it is never asked in.
                if (role is { } staff && staff != parent.GuildId)
                {
                    roleIds.Add(staff);
                    mentions += $" <@&{Text(staff)}>";
                }

                await thread.SendMessageAsync(
                        text: $"{mentions}\n{opening.Text}",
                        allowedMentions: new AllowedMentions { UserIds = userIds, RoleIds = roleIds },
                        components: Buttons(null, opening.Actions) is { Components.Count: > 0 } buttons ? buttons : null,
                        options: Request(ct))
                    .ConfigureAwait(false);

                return DiscordPostOutcome.Posted(Text(thread.Id));
            }
            catch
            {
                // A ticket with no message and no buttons would be a dead end for the member.
                try
                {
                    await thread.DeleteAsync(Request(ct)).ConfigureAwait(false);
                }
                catch (Exception cleanUp) when (cleanUp is not OperationCanceledException)
                {
                    _log.Debug(cleanUp, "Could not delete a ticket thread that was never written in");
                }

                throw;
            }
        }).ConfigureAwait(false);

        return made.Sent && made.MessageId is { } threadId
            ? DiscordTicketOutcome.Made(threadId)
            : DiscordTicketOutcome.Failed(made.Error ?? "Discord did not make the ticket.");
    }

    /// <summary>Adds a member to a private thread. A refusal is logged: the first message mentions them, which adds them too.</summary>
    private async Task AddToThreadAsync(IThreadChannel thread, ulong guildId, ulong memberId, CancellationToken ct)
    {
        try
        {
            var guild = _client.GetGuild(guildId);
            IGuildUser? user = guild?.GetUser(memberId);

            if (user is null && guild is not null)
                user = await ((IGuild)guild).GetUserAsync(memberId, CacheMode.AllowDownload, Request(ct)).ConfigureAwait(false);

            if (user is not null)
                await thread.AddUserAsync(user, Request(ct)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not add the member to a ticket thread; the first message mentions them");
        }
    }

    public Task<DiscordPostOutcome> CloseTicketThreadAsync(string threadId, string line, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return InChannelAsync(threadId, ChannelNeed.TicketClose, async channel =>
        {
            if (channel is not IThreadChannel thread)
                return DiscordPostOutcome.Failed("That channel is not a thread.", permanent: true, notFound: true);

            await thread.SendMessageAsync(text: line, allowedMentions: AllowedMentions.None, options: Request(ct)).ConfigureAwait(false);

            // Locked, so the member cannot write in it again; archived, so it leaves the thread list.
            await thread.ModifyAsync(p =>
            {
                p.Locked = true;
                p.Archived = true;
            }, Request(ct)).ConfigureAwait(false);

            return DiscordPostOutcome.Ok;
        });
    }

    public Task<DiscordPostOutcome> DeleteTicketThreadAsync(string threadId, string reason, CancellationToken ct)
        => InChannelAsync(threadId, ChannelNeed.TicketDelete, async channel =>
        {
            if (channel is not IThreadChannel thread)
                return DiscordPostOutcome.Failed("That channel is not a thread.", permanent: true, notFound: true);

            await thread.DeleteAsync(new RequestOptions { AuditLogReason = reason, CancelToken = ct }).ConfigureAwait(false);
            return DiscordPostOutcome.Ok;
        });

    public async Task<bool?> TicketThreadExistsAsync(string threadId, CancellationToken ct)
    {
        if (ParseId(threadId) is not { } id)
            return false;

        try
        {
            return _client.GetChannel(id) is not null
                || await _client.GetChannelAsync(id, Request(ct)).ConfigureAwait(false) is not null;
        }
        catch (HttpException e) when (e.HttpCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Debug(e, "Could not ask Discord whether a ticket thread is still there");
            return null;
        }
    }

    public Task<IReadOnlyList<string>?> MissingTicketPermissionsAsync(string channelId, CancellationToken ct)
    {
        if (ParseId(channelId) is not { } id
            || _client.GetChannel(id) is not SocketGuildChannel channel
            || channel.Guild.CurrentUser is not { } me)
        {
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }

        var has = me.GetPermissions(channel);
        var missing = new List<string>();

        if (!has.ViewChannel) missing.Add("View Channels");
        if (!has.SendMessages) missing.Add("Send Messages");
        if (!has.CreatePrivateThreads) missing.Add("Create Private Threads");
        if (!has.SendMessagesInThreads) missing.Add("Send Messages in Threads");
        if (!has.ManageThreads) missing.Add("Manage Threads");
        if (!has.ReadMessageHistory) missing.Add("Read Message History");

        return Task.FromResult<IReadOnlyList<string>?>(missing);
    }
}
