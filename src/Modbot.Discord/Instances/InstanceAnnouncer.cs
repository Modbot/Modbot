using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Instances;

public enum InstanceAnnouncePassOutcome
{
    /// <summary>No channel is set. Nothing read, nothing posted.</summary>
    NoChannel = 1,

    /// <summary>Nothing to say: no room opened, and none was due an update.</summary>
    NothingToSay = 2,

    /// <summary>At least one message was written or rewritten.</summary>
    Posted = 3,

    /// <summary>Discord refused. Whatever had already gone out stands.</summary>
    Failed = 4,
}

/// <param name="Announced">Rooms whose card was posted for the first time.</param>
/// <param name="Updated">Cards brought up to date.</param>
/// <param name="Finished">Cards given their last word because the room closed.</param>
public sealed record InstanceAnnouncePass(
    InstanceAnnouncePassOutcome Outcome,
    int Announced = 0,
    int Updated = 0,
    int Finished = 0,
    string? Error = null);

/// <summary>
/// Keeps a Discord channel showing which rooms the group has open, one card each.
/// </summary>
/// <remarks>
/// <para>
/// A room gets one message when it opens. That message is then rewritten as the room fills and
/// empties, and rewritten a last time when the room closes -- after which it is never touched
/// again. One message per room rather than a message per change, because a channel that posts
/// every time somebody walks in is a channel people mute.
/// </para>
/// <para>
/// <strong>Only the group's own rooms are announced.</strong> A moderator's private or public
/// instance is somewhere they happen to be, not something the group is running, and posting it
/// would tell a channel where a specific person is. The group's live list is exactly the right
/// filter for this and it is already being polled.
/// </para>
/// <para>
/// <strong>Turning the channel on announces nothing that is already running.</strong> Only a room
/// that opened within <see cref="AnnounceWithin"/> gets a card, so pasting a channel id does not
/// fill it with notices for an evening three hours in progress -- the same protection the
/// moderation log gives with its cursor. It also covers the case where Modbot itself was down:
/// coming back up does not announce rooms that have been running the whole time.
/// </para>
/// <para>
/// <strong>Names.</strong> While a moderator is watching a room, its card lists who is there
/// (<see cref="InstanceCard"/>), from the same watching rule the Live page and the overlay use
/// (<see cref="RoomWatching"/>). Nobody watching, or <c>DiscordInstanceShowNames</c> off, and the
/// card carries the head count only.
/// </para>
/// <para>
/// <strong>Pacing.</strong> Discord's limits on editing are real and per-channel, so a pass does
/// a handful of messages and no more, and a room's card is rewritten at most once a minute
/// however often the instance poll runs. A refused message leaves the row untouched, so the next
/// pass tries the same thing rather than skipping it -- except on a permanent refusal, where the
/// message is gone or the bot has lost the channel, and the room's announcement is forgotten.
/// </para>
/// </remarks>
public sealed class InstanceAnnouncer
{
    /// <summary>How often one room's card is allowed to be rewritten.</summary>
    /// <remarks>
    /// The instance poll runs every ten seconds, but a card that changes six times a minute is
    /// both unreadable and a fast way to meet Discord's edit limits. A minute is frequent enough
    /// that "people here now" is honest and slow enough that nobody notices the lag.
    /// </remarks>
    public static readonly TimeSpan RewriteEvery = TimeSpan.FromMinutes(1);

    /// <summary>How many messages one pass will send or rewrite.</summary>
    public const int MessagesPerPass = 5;

    /// <summary>
    /// How new a room has to be to be worth announcing at all.
    /// </summary>
    /// <remarks>
    /// "Come and join us" is worth saying about a room that just opened and pointless about one
    /// that has been running since teatime. This is what stops a freshly configured channel -- or
    /// a Modbot coming back after an outage -- from posting a wall of cards for rooms everybody
    /// already knows about.
    /// </remarks>
    public static readonly TimeSpan AnnounceWithin = TimeSpan.FromMinutes(15);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly ILogger _log;

    public InstanceAnnouncer(
        ModbotContext db,
        IModbotClock clock,
        DiscordBotStatus status,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(status);

        _db = db;
        _clock = clock;
        _status = status;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<InstanceAnnouncePass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.DiscordInstanceChannelId is not { Length: > 0 } channelId)
            return new InstanceAnnouncePass(InstanceAnnouncePassOutcome.NoChannel);

        var now = _clock.UtcNow;
        var message = Trim(settings.DiscordInstanceMessage);

        // Rooms that still have something to say: open ones, and ones that closed without their
        // card having been given its last word. The filtered index answers this.
        var rooms = await _db.VRChatInstances
            .Where(i => !i.AnnouncementFinished
                && i.SeenInGroupList
                && (i.ClosedAt == null || i.AnnouncementMessageId != null))
            .OrderBy(i => i.AnnouncementUpdatedAt)
            .Take(MessagesPerPass * 4)
            .ToListAsync(ct).ConfigureAwait(false);

        var names = settings.DiscordInstanceShowNames
            ? await NamesAsync(rooms, ct).ConfigureAwait(false)
            : [];

        var announced = 0;
        var updated = 0;
        var finished = 0;
        string? error = null;
        var sent = 0;

        foreach (var room in rooms)
        {
            if (sent >= MessagesPerPass)
                break;

            var closed = room.ClosedAt is not null;

            if (room.AnnouncementMessageId is null)
            {
                // A room that closed before it was ever announced is not worth a card saying an
                // evening nobody heard about has ended.
                if (closed)
                {
                    room.AnnouncementFinished = true;
                    continue;
                }

                // Too late to be news. Marked finished so it is never reconsidered rather than
                // being looked at again on every pass for the rest of its life.
                if (now - room.OpenedAt > AnnounceWithin)
                {
                    room.AnnouncementFinished = true;
                    continue;
                }

                var outcome = await AnnounceAsync(
                    gateway, room, channelId, message, names.GetValueOrDefault(room.Id), now, ct).ConfigureAwait(false);
                sent++;

                if (outcome.Sent)
                {
                    announced++;
                    continue;
                }

                error ??= outcome.Error;

                // A permanent refusal is about the channel, not this room: the id is wrong, or the
                // bot cannot post there. Trying the next room would produce the same answer.
                if (outcome.Permanent)
                    break;

                continue;
            }

            if (!closed && room.AnnouncementUpdatedAt is { } written && now - written < RewriteEvery)
                continue;

            var edit = await RewriteAsync(gateway, room, message, names.GetValueOrDefault(room.Id), now, ct).ConfigureAwait(false);
            sent++;

            if (edit.Sent)
            {
                if (closed)
                    finished++;
                else
                    updated++;

                continue;
            }

            error ??= edit.Error;

            if (edit.Permanent)
            {
                // Somebody deleted the message, or the bot lost the channel. Forget the card
                // rather than trying to rewrite a message that is not there every minute forever.
                _log.Information(
                    "Forgetting the Discord card for an instance: {Reason}", edit.Error ?? "it is gone");

                room.AnnouncementMessageId = null;
                room.AnnouncementChannelId = null;
                room.AnnouncementFinished = true;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (announced + updated + finished > 0)
            _status.Posted(announced + updated + finished, now);

        if (error is not null && announced + updated + finished == 0)
            return new InstanceAnnouncePass(InstanceAnnouncePassOutcome.Failed, Error: error);

        if (announced + updated + finished == 0)
            return new InstanceAnnouncePass(InstanceAnnouncePassOutcome.NothingToSay);

        return new InstanceAnnouncePass(
            InstanceAnnouncePassOutcome.Posted, announced, updated, finished, error);
    }

    private async Task<DiscordPostOutcome> AnnounceAsync(
        IDiscordGateway gateway,
        VRChatInstance room,
        string channelId,
        string? message,
        IReadOnlyList<string?>? names,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var card = InstanceCard.For(room, await WorldOfAsync(room, ct).ConfigureAwait(false), now, names);

        var outcome = await gateway.PostAsync(channelId, message, [card], ct).ConfigureAwait(false);

        if (!outcome.Sent || outcome.MessageId is null)
            return outcome;

        room.AnnouncementMessageId = outcome.MessageId;
        room.AnnouncementChannelId = channelId;
        room.AnnouncementUpdatedAt = now;

        return outcome;
    }

    private async Task<DiscordPostOutcome> RewriteAsync(
        IDiscordGateway gateway,
        VRChatInstance room,
        string? message,
        IReadOnlyList<string?>? names,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // The channel the message is actually in, not the channel the setting names now: an
        // operator who moved the setting has not moved the messages already posted.
        var channelId = room.AnnouncementChannelId;

        if (channelId is not { Length: > 0 } || room.AnnouncementMessageId is not { Length: > 0 } messageId)
            return DiscordPostOutcome.Failed("The card has no message to rewrite.", permanent: true);

        var card = InstanceCard.For(room, await WorldOfAsync(room, ct).ConfigureAwait(false), now, names);

        var outcome = await gateway.EditAsync(channelId, messageId, message, [card], ct).ConfigureAwait(false);

        if (!outcome.Sent)
            return outcome;

        room.AnnouncementUpdatedAt = now;

        if (room.ClosedAt is not null)
            room.AnnouncementFinished = true;

        return outcome;
    }

    /// <summary>
    /// The display names of the people in each open room a moderator is watching. Rooms nobody is
    /// watching are absent, so their cards show the head count only.
    /// </summary>
    /// <remarks>
    /// A person the facts carried no name for is named from their stored profile when there is one,
    /// and otherwise left as a null the card counts in "and N more" -- never shown by id.
    /// </remarks>
    private async Task<Dictionary<Guid, IReadOnlyList<string?>>> NamesAsync(
        IReadOnlyList<VRChatInstance> rooms,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string?>>();
        var open = rooms.Where(r => r.ClosedAt is null).ToList();

        if (open.Count == 0)
            return result;

        var people = await new RoomPeopleReader(_db).ForRoomsAsync(open, ct).ConfigureAwait(false);

        var nameless = people.Values
            .Where(p => p.IsWatched)
            .SelectMany(p => p.Here)
            .Where(p => p.DisplayName is null)
            .Select(p => p.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var stored = nameless.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _db.VRChatUsers.AsNoTracking()
                .Where(u => nameless.Contains(u.UserId) && u.DisplayName != null)
                .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

        foreach (var (roomId, inRoom) in people)
        {
            if (inRoom.IsWatched)
                result[roomId] = inRoom.Here.Select(p => p.DisplayName ?? stored.GetValueOrDefault(p.UserId)).ToList();
        }

        return result;
    }

    private Task<VRChatWorld?> WorldOfAsync(VRChatInstance room, CancellationToken ct) =>
        _db.VRChatWorlds.AsNoTracking().FirstOrDefaultAsync(w => w.WorldId == room.WorldId, ct);

    private static string? Trim(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : message.Trim();
}
