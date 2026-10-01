using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Cards;
using Modbot.Discord.Instances;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <param name="Calls">How many Discord calls the pass made.</param>
/// <param name="Error">The first refusal, when there was one.</param>
public sealed record CalendarDiscordPass(int Calls, string? Error = null);

/// <summary>
/// Keeps each event's Discord server event and channel post in line with the event (calendar design
/// §3.2, §3.3).
/// </summary>
/// <remarks>
/// <para>
/// Reads what the VRChat side wrote -- the event's state and current occurrence, and the instance it
/// opened -- and asks VRChat nothing, the same split as the instance announcer.
/// </para>
/// <para>
/// <strong>Each place is written only when what it should say changes</strong>, by comparing a
/// fingerprint with the one last sent. So an edit, the instance opening, the event finishing and a
/// cancel all arrive by the same path, and a quiet pass costs no Discord calls at all.
/// </para>
/// <para>
/// <strong>Each occurrence is its own server event and its own post.</strong> Discord cannot move an
/// event backwards from started to scheduled, and a notice board says "tonight", not "every
/// Friday": when a repeating event moves on, the old server event is ended, the old post gets its
/// last word, and new ones are made for the next occurrence.
/// </para>
/// <para>
/// A refusal that will not change on its own -- the bot lacks Manage Events, the channel is gone --
/// is not repeated until the event changes. Anything else is tried again on the next pass.
/// </para>
/// <para>
/// <strong>A place whose state turns to published or removed writes a fact</strong>, and only
/// then: an edit sent to a place that is already published writes none. The fact is what the live
/// stream carries, so the calendar page shows "Published" without a reload (added 2026-10-01; the
/// docs had promised it and nothing sent it). A failure already wrote one.
/// </para>
/// <para>
/// <strong>The cancel post</strong> (<see cref="CalendarPlaces.CancelPost"/>) is a row the cancel
/// itself makes, only when the moderator ticked it. It is posted once: the row then holds the
/// message's id, and nothing edits or posts it again.
/// </para>
/// </remarks>
public sealed class CalendarDiscordPublisher
{
    /// <summary>How many Discord calls one pass may make.</summary>
    public const int CallsPerPass = 5;

    /// <summary>Discord refuses a server event that starts in the past; one that is late starts this far ahead.</summary>
    public static readonly TimeSpan LateStartAhead = TimeSpan.FromMinutes(1);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly CardPictures _pictures;
    private readonly ILogger _log;

    /// <summary>Places this pass made, for the state-change facts.</summary>
    private readonly List<CalendarEventPlace> _added = [];

    public CalendarDiscordPublisher(
        ModbotContext db,
        IModbotClock clock,
        DiscordBotStatus status,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        CardPictures? pictures = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _status = status;
        _facts = facts;
        _partitions = partitions;
        _pictures = pictures ?? new CardPictures();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<CalendarDiscordPass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var guildId = settings.DiscordGuildId?.Trim();
        var publicAddress = settings.PublicAddress?.TrimEnd('/');
        var now = _clock.UtcNow;

        var places = await _db.CalendarEventPlaces
            .Where(p => (p.Place == CalendarPlaces.DiscordEvent || p.Place == CalendarPlaces.ChannelPost)
                && p.State != CalendarPlaceStates.Removed)
            .ToListAsync(ct).ConfigureAwait(false);

        // Waiting, or a failure that was not a refusal: a refusal is not sent again.
        var cancelPosts = await _db.CalendarEventPlaces
            .Where(p => p.Place == CalendarPlaces.CancelPost
                && (p.State == CalendarPlaceStates.Waiting
                    || (p.State == CalendarPlaceStates.Failed && p.FailedFingerprint == null)))
            .ToListAsync(ct).ConfigureAwait(false);

        // One date of a repeating event cancelled with the channel post ticked, not posted yet (§2.2).
        var dateCancelPosts = await _db.CalendarDateChanges
            .Where(c => c.Cancelled && c.CancelPostChannelId != null && c.CancelPostId == null)
            .ToListAsync(ct).ConfigureAwait(false);

        // What each place said before this pass, and whether Discord held a copy of it, so a first
        // publish or a take-down can be told from an edit.
        var was = places.Concat(cancelPosts)
            .ToDictionary<CalendarEventPlace, CalendarEventPlace, (string State, bool InDiscord)>(
                p => p, p => (p.State, p.ExternalId is not null), ReferenceEqualityComparer.Instance);

        var withPlaces = places.Select(p => p.EventId).Distinct().ToList();

        var events = await _db.CalendarEvents
            .Where(e => withPlaces.Contains(e.Id)
                || ((e.PublishToDiscord || e.PostToChannel)
                    && e.DeletedAt == null
                    && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)))
            .OrderBy(e => e.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (events.Count == 0 && cancelPosts.Count == 0 && dateCancelPosts.Count == 0)
            return new CalendarDiscordPass(0);

        var worldIds = events.Where(e => e.WorldId != null).Select(e => e.WorldId!).Distinct().ToList();
        var worlds = await _db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var pass = new Pass(
            gateway,
            guildId,
            publicAddress,
            now,
            worlds,
            new CardStyle(publicAddress, settings.ManagedGroupName),
            settings.VRChatImagesProxied,
            _pictures,
            ct);

        // First: a cancel post is news, and there are few of them.
        await SyncCancelPostsAsync(pass, cancelPosts).ConfigureAwait(false);
        await SyncDateCancelPostsAsync(pass, dateCancelPosts).ConfigureAwait(false);

        foreach (var calendarEvent in events)
        {
            if (pass.Calls >= CallsPerPass)
                break;

            var joinLink = await JoinLinkAsync(calendarEvent, ct).ConfigureAwait(false);
            var eventPlace = places.FirstOrDefault(p => p.EventId == calendarEvent.Id && p.Place == CalendarPlaces.DiscordEvent);
            var postPlace = places.FirstOrDefault(p => p.EventId == calendarEvent.Id && p.Place == CalendarPlaces.ChannelPost);

            await SyncServerEventAsync(pass, calendarEvent, eventPlace, joinLink).ConfigureAwait(false);

            if (pass.Calls >= CallsPerPass)
                break;

            await SyncPostAsync(pass, calendarEvent, postPlace, joinLink).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var (calendarEvent, place, error) in pass.Failures)
        {
            await WriteFactAsync(
                FactType.PlannedEventPublishFailed,
                calendarEvent,
                new JsonObject { ["title"] = calendarEvent.Title, ["place"] = place, ["error"] = error },
                now,
                ct).ConfigureAwait(false);
        }

        var byId = events.Concat(pass.CancelledEvents).DistinctBy(e => e.Id).ToDictionary(e => e.Id);

        foreach (var place in places.Concat(cancelPosts).Concat(_added))
        {
            var (before, inDiscord) = was.TryGetValue(place, out var seen) ? seen : (null, false);

            if (place.State == before || !byId.TryGetValue(place.EventId, out var calendarEvent))
                continue;

            // Written when a place gets onto Discord for the first time or comes off it, not for
            // every edit: an edit that goes through waiting or a failure and back is not "published"
            // again, and a place Discord never held has nothing to be taken down from.
            if (place.State == CalendarPlaceStates.Published && !inDiscord)
            {
                await WriteFactAsync(
                    FactType.PlannedEventPublished,
                    calendarEvent,
                    new JsonObject { ["title"] = calendarEvent.Title, ["place"] = place.Place },
                    now,
                    ct).ConfigureAwait(false);
            }
            else if (place.State == CalendarPlaceStates.Removed && inDiscord)
            {
                await WriteFactAsync(
                    FactType.PlannedEventTakenDown,
                    calendarEvent,
                    new JsonObject { ["title"] = calendarEvent.Title, ["place"] = place.Place, ["was"] = before },
                    now,
                    ct).ConfigureAwait(false);
            }
        }

        if (pass.Written > 0)
            _status.Posted(pass.Written, now);

        return new CalendarDiscordPass(pass.Calls, pass.Failures.FirstOrDefault().Error);
    }

    // ── The server event ─────────────────────────────────────────────────────────────────

    private async Task SyncServerEventAsync(Pass pass, CalendarEvent e, CalendarEventPlace? place, string? joinLink)
    {
        var wants = e.PublishToDiscord
            && e.DeletedAt is null
            && CalendarEventStates.IsLive(e.State)
            && e.OccurrenceStartsAt is not null
            && pass.GuildId is { Length: > 0 };

        // The date it is about, known by its planned start: a date moved on its own keeps its own
        // Discord event and has it updated, rather than ended and made again (calendar design §2.2).
        var occurrence = CalendarRepeat.Current(e);

        // The occurrence moved on -- or the date was cancelled on its own, which moves it on too --
        // or the event is not wanted in Discord any more: end the one there is.
        if (place?.ExternalId is { } existing && (!wants || place.OccurrenceStartsAt != occurrence.PlannedStartsAt))
        {
            var guild = place.ChannelId ?? pass.GuildId ?? string.Empty;
            var ended = await pass.Call(g => g.EndEventAsync(guild, existing, pass.Ct)).ConfigureAwait(false);

            if (!ended.Sent)
            {
                Fail(pass, e, place, ended, fingerprint: null);
                return;
            }

            place.ExternalId = null;
            place.SentFingerprint = null;
            place.FailedFingerprint = null;
            place.Error = null;
            place.State = wants ? CalendarPlaceStates.Waiting : CalendarPlaceStates.Removed;
            place.UpdatedAt = pass.Now;
            pass.Written++;
        }

        if (!wants)
        {
            if (place is { ExternalId: null } && place.State != CalendarPlaceStates.Removed)
            {
                place.State = CalendarPlaceStates.Removed;
                place.UpdatedAt = pass.Now;
            }

            return;
        }

        if (pass.Calls >= CallsPerPass)
            return;

        place ??= AddPlace(e, CalendarPlaces.DiscordEvent);

        var open = e.State == CalendarEventStates.Open;
        var title = CalendarRepeat.TitleOf(e, occurrence);
        var description = CalendarRepeat.DescriptionOf(e, occurrence);
        var world = pass.WorldOf(e);

        // Modbot's short address only leads somewhere while there is a join link behind it: an
        // instance Modbot opened for this occurrence that has not closed. Until 2026-10-01 it was
        // used whenever the event was open, so an event with no opened instance -- opening it
        // automatically turned off, or the opening refused -- showed a link that answered 404.
        // Without a join link the location stays the world's name, the same rule the description's
        // "Join:" line and the post's Join button already follow.
        var location = Location(pass.PublicAddress, e, joinLink);

        var fingerprint = CalendarFingerprint.Of(
            "discordEvent", title, description, occurrence.StartsAt, occurrence.EndsAt,
            location, joinLink, open, e.ImageUrl, world?.Name, world?.ImageUrl, e.WorldId);

        if (place.ExternalId is not null && place.SentFingerprint == fingerprint)
            return;

        if (place.State == CalendarPlaceStates.Failed && place.FailedFingerprint == fingerprint)
            return;

        var details = ServerEventDetails(e, world, pass.PublicAddress, joinLink, pass.Now);
        var guildId = pass.GuildId!;

        DiscordPostOutcome outcome;

        if (place.ExternalId is null)
        {
            outcome = await pass.Call(g => g.CreateEventAsync(guildId, details, pass.Ct)).ConfigureAwait(false);

            if (outcome is { Sent: true, MessageId: { } created })
            {
                place.ExternalId = created;
                place.ChannelId = guildId;
                place.OccurrenceStartsAt = occurrence.PlannedStartsAt;

                // Opened already: start it now rather than on the next pass.
                if (open && pass.Calls < CallsPerPass)
                    outcome = await pass.Call(g => g.UpdateEventAsync(guildId, created, details, start: true, pass.Ct)).ConfigureAwait(false);
            }
        }
        else
        {
            var id = place.ExternalId;
            outcome = await pass.Call(g => g.UpdateEventAsync(guildId, id, details, start: open, pass.Ct)).ConfigureAwait(false);

            // Somebody deleted or ended it in Discord. Forget it; the next pass makes it again.
            if (!outcome.Sent && outcome.Permanent && outcome.Error == DiscordScheduledEventDetails.Gone)
            {
                place.ExternalId = null;
                place.SentFingerprint = null;
                place.State = CalendarPlaceStates.Waiting;
                place.UpdatedAt = pass.Now;
                return;
            }
        }

        if (!outcome.Sent)
        {
            Fail(pass, e, place, outcome, fingerprint);
            return;
        }

        Published(place, fingerprint, pass.Now);
        pass.Written++;
    }

    /// <summary>
    /// The server event as Discord is sent it, for the event's current occurrence. The form's
    /// preview is drawn from this too, so the two cannot disagree.
    /// </summary>
    /// <param name="joinLink">The open instance's join link, or null; only used while the event is open.</param>
    /// <param name="now">From <c>IModbotClock</c>. Discord refuses a start in the past, so a late one starts a minute from now.</param>
    public static DiscordScheduledEventDetails ServerEventDetails(
        CalendarEvent calendarEvent, VRChatWorld? world, string? publicAddress, string? joinLink, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        // The date it is about, with its own times and words when it was changed on its own (§2.2).
        var occurrence = CalendarRepeat.Current(calendarEvent);
        var open = calendarEvent.State == CalendarEventStates.Open;
        var link = open ? joinLink : null;
        var startsAt = occurrence.StartsAt > now ? occurrence.StartsAt : now + LateStartAhead;

        return CalendarCard.EventDetails(
            calendarEvent,
            world,
            startsAt,
            occurrence.EndsAt,
            Location(publicAddress, calendarEvent, link),
            link,
            CalendarRepeat.TitleOf(calendarEvent, occurrence),
            CalendarRepeat.DescriptionOf(calendarEvent, occurrence));
    }

    /// <summary>
    /// The channel post's card and buttons for the event's current occurrence, with the picture the
    /// caller worked out. The form's preview is drawn from this too.
    /// </summary>
    public static (DiscordEmbedContent Card, IReadOnlyList<DiscordLinkButton> Links) Post(
        CalendarEvent calendarEvent, VRChatWorld? world, string? joinLink, CardStyle style, string? picture)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var state = calendarEvent.State == CalendarEventStates.Open ? CalendarCardState.Open : CalendarCardState.Scheduled;
        var card = CalendarCard.For(
            calendarEvent, CalendarRepeat.Current(calendarEvent), world, state, joinLink, style, new CardPicture(Image: picture));

        return (card, CalendarCard.Links(state, joinLink));
    }

    /// <summary>
    /// What goes in the server event's location field, before <see cref="CalendarCard.EventDetails"/>
    /// falls back to the world's name: the short address while there is a join link behind it, or
    /// the join link itself; null without one. The preview in the form uses this too.
    /// </summary>
    public static string? Location(string? publicAddress, CalendarEvent calendarEvent, string? joinLink) =>
        joinLink is not null ? ShortJoinAddress(publicAddress, calendarEvent) ?? joinLink : null;

    /// <summary>
    /// Modbot's own short address that sends a person on to the join link, when the public address is
    /// known and the whole thing fits Discord's location field.
    /// </summary>
    public static string? ShortJoinAddress(string? publicAddress, CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (string.IsNullOrWhiteSpace(publicAddress))
            return null;

        var address = $"{publicAddress.TrimEnd('/')}/api/calendar/join/{calendarEvent.Id:D}";
        return address.Length <= CalendarCard.DiscordEventLocationLimit ? address : null;
    }

    // ── The channel post ─────────────────────────────────────────────────────────────────

    private async Task SyncPostAsync(Pass pass, CalendarEvent e, CalendarEventPlace? place, string? joinLink)
    {
        var channelId = e.ChannelId?.Trim();
        var removedInModbot = e.State == CalendarEventStates.Cancelled || e.DeletedAt is not null;
        var current = CalendarRepeat.Current(e);

        if (place?.ExternalId is { } messageId && place.ChannelId is { } postedIn)
        {
            var movedOn = CalendarEventStates.IsLive(e.State) && place.OccurrenceStartsAt != current.PlannedStartsAt;
            var ended = e.State == CalendarEventStates.Finished || removedInModbot || movedOn;
            var unticked = !removedInModbot && (!e.PostToChannel || channelId != postedIn || e.State == CalendarEventStates.Draft);

            if (unticked)
            {
                var deleted = await pass.Call(g => g.DeleteMessageAsync(postedIn, messageId, "Calendar post turned off", pass.Ct)).ConfigureAwait(false);

                if (!deleted.Sent && !deleted.Permanent)
                {
                    Fail(pass, e, place, deleted, fingerprint: null);
                    return;
                }

                Forget(place, CalendarPlaceStates.Removed, pass.Now);
                pass.Written++;
            }
            else if (ended)
            {
                // The post's last word, about the date it was made for: "Cancelled" for a date
                // cancelled on its own as well as for the whole event.
                var dateCancelled = place.OccurrenceStartsAt is { } posted
                    && e.DateChanges.Any(c => c.Cancelled && c.PlannedStartsAt == posted);
                var state = removedInModbot || dateCancelled ? CalendarCardState.Cancelled : CalendarCardState.Finished;
                // At the times the post last showed: a date moved and then cancelled keeps its move.
                var occurrence = place.OccurrenceStartsAt is { } was
                    ? e.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == was) is { } change
                        ? CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e))
                        : new CalendarOccurrence(was, was + CalendarRepeat.LengthOf(e))
                    : current;

                // The picture is already on the message and stays there, so the last word costs
                // no upload.
                var closingWorld = pass.WorldOf(e);
                var closingPictures = pass.Pictures();
                var closingPicture = new CardPicture(
                    Image: CalendarCard.OwnPicture(e)
                        ?? await closingPictures.ReferenceAsync(InstanceCard.PictureOf(closingWorld), pass.Ct).ConfigureAwait(false));

                var card = CalendarCard.For(
                    e, occurrence, closingWorld, state, joinLink: null, pass.Style, closingPicture);

                var edited = await pass
                    .Call(g => g.EditAsync(postedIn, messageId, null, [card], [], pictures: null, pass.Ct))
                    .ConfigureAwait(false);

                if (!edited.Sent && !edited.Permanent)
                {
                    Fail(pass, e, place, edited, fingerprint: null);
                    return;
                }

                Forget(place, movedOn ? CalendarPlaceStates.Waiting : CalendarPlaceStates.Removed, pass.Now);
                pass.Written++;

                if (!movedOn)
                    return;
            }
        }

        var wants = e.PostToChannel
            && channelId is { Length: > 0 }
            && e.DeletedAt is null
            && CalendarEventStates.IsLive(e.State)
            && e.OccurrenceStartsAt is not null;

        if (!wants)
        {
            if (place is { ExternalId: null } && place.State != CalendarPlaceStates.Removed)
            {
                place.State = CalendarPlaceStates.Removed;
                place.UpdatedAt = pass.Now;
            }

            return;
        }

        if (pass.Calls >= CallsPerPass)
            return;

        place ??= AddPlace(e, CalendarPlaces.ChannelPost);

        var world = pass.WorldOf(e);
        var first = place.ExternalId is null;

        // A first post sends the world's picture; an edit points at the file the first post left
        // on the message. The event's own picture, if the moderators gave one, is an ordinary
        // address on a host that serves anybody, so it is linked either way.
        var pictures = pass.Pictures();
        var image = CalendarCard.OwnPicture(e)
            ?? (first
                ? await pictures.AddAsync(InstanceCard.PictureOf(world), pass.Ct).ConfigureAwait(false)
                : await pictures.ReferenceAsync(InstanceCard.PictureOf(world), pass.Ct).ConfigureAwait(false));

        var (embed, links) = Post(e, world, joinLink, pass.Style, image);

        var fingerprint = CalendarFingerprint.Of(
            "channelPost", channelId, embed.Title, embed.Description, embed.Color, embed.Url, embed.Footer, embed.ImageUrl,
            string.Join('\n', embed.Fields.Select(f => f.Name + "=" + f.Value)), links.Count > 0 ? links[0].Url : null);

        if (place.ExternalId is not null && place.SentFingerprint == fingerprint)
            return;

        if (place.State == CalendarPlaceStates.Failed && place.FailedFingerprint == fingerprint)
            return;

        DiscordPostOutcome outcome;

        if (first)
        {
            outcome = await pass
                .Call(g => g.PostAsync(channelId!, null, [embed], links, pictures.Files, pass.Ct))
                .ConfigureAwait(false);

            if (outcome is { Sent: true, MessageId: { } posted })
            {
                place.ExternalId = posted;
                place.ChannelId = channelId;
                place.OccurrenceStartsAt = current.PlannedStartsAt;
            }
        }
        else
        {
            var id = place.ExternalId!;
            var inChannel = place.ChannelId ?? channelId!;
            outcome = await pass
                .Call(g => g.EditAsync(inChannel, id, null, [embed], links, pictures: null, pass.Ct))
                .ConfigureAwait(false);

            // Somebody deleted the post. Not posted again until the event changes, so a moderator who
            // deleted it on purpose is not argued with every twenty seconds.
            if (!outcome.Sent && outcome.Permanent)
            {
                place.ExternalId = null;
                place.SentFingerprint = null;
            }
        }

        if (!outcome.Sent)
        {
            Fail(pass, e, place, outcome, fingerprint);
            return;
        }

        Published(place, fingerprint, pass.Now);
        pass.Written++;
    }

    // ── The cancel post ──────────────────────────────────────────────────────────────────

    /// <summary>The fingerprint a refused cancel post is kept under, so it is not sent again.</summary>
    private const string CancelPostFingerprint = "cancelPost";

    private async Task SyncCancelPostsAsync(Pass pass, List<CalendarEventPlace> cancelPosts)
    {
        if (cancelPosts.Count == 0)
            return;

        var ids = cancelPosts.Select(p => p.EventId).ToList();
        var cancelled = await _db.CalendarEvents
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, pass.Ct).ConfigureAwait(false);

        foreach (var place in cancelPosts)
        {
            if (pass.Calls >= CallsPerPass)
                return;

            // A row that holds a message id has been posted: never twice, whatever its state says.
            if (place.ExternalId is not null || !cancelled.TryGetValue(place.EventId, out var e))
                continue;

            pass.CancelledEvents.Add(e);

            if (place.ChannelId is not { Length: > 0 } channelId)
            {
                place.State = CalendarPlaceStates.Removed;
                place.UpdatedAt = pass.Now;
                continue;
            }

            var startsAt = place.OccurrenceStartsAt ?? e.OccurrenceStartsAt ?? e.StartsAt;
            var outcome = await pass
                .Call(g => g.PostAsync(channelId, CalendarCard.CancelNotice(e, startsAt), [], null, pass.Ct))
                .ConfigureAwait(false);

            if (!outcome.Sent)
            {
                Fail(pass, e, place, outcome, CancelPostFingerprint);
                continue;
            }

            place.ExternalId = outcome.MessageId ?? string.Empty;
            Published(place, CancelPostFingerprint, pass.Now);
            pass.Written++;
        }
    }

    /// <summary>
    /// The cancel post for one date cancelled on its own, when the moderator ticked it: the same
    /// message a whole-event cancel posts, with that date's time, posted once. A refusal is not
    /// sent again; anything else is tried on the next pass.
    /// </summary>
    /// <summary>How long after a cancelled date ended its cancel post is still worth posting.</summary>
    public static readonly TimeSpan DateCancelPostKeptFor = TimeSpan.FromDays(1);

    private async Task SyncDateCancelPostsAsync(Pass pass, List<CalendarDateChange> dates)
    {
        if (dates.Count == 0)
            return;

        var ids = dates.Select(c => c.EventId).Distinct().ToList();
        var owners = await _db.CalendarEvents
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, pass.Ct).ConfigureAwait(false);

        foreach (var date in dates)
        {
            if (pass.Calls >= CallsPerPass)
                return;

            if (!owners.TryGetValue(date.EventId, out var e) || date.CancelPostChannelId is not { Length: > 0 } channelId)
                continue;

            var occurrence = CalendarRepeat.Changed(date, CalendarRepeat.LengthOf(e));

            // News only while it is news: not for an event deleted since, nor a date long over (a
            // pass that could not post for a while, then catches up).
            if (e.DeletedAt is not null || occurrence.EndsAt < pass.Now - DateCancelPostKeptFor)
            {
                date.CancelPostChannelId = null;
                continue;
            }

            var notice = CalendarCard.CancelNotice(e, occurrence.StartsAt, CalendarRepeat.TitleOf(e, occurrence));
            var outcome = await pass
                .Call(g => g.PostAsync(channelId, notice, [], null, pass.Ct))
                .ConfigureAwait(false);

            if (!outcome.Sent)
            {
                var error = outcome.Error ?? "Discord refused.";
                _log.Warning("Could not post that one date of the event {EventId} is cancelled: {Reason}", e.Id, error);

                // Refused: not sent again, and said once as a failed place. Anything else waits for
                // the next pass.
                if (outcome.Permanent)
                {
                    date.CancelPostChannelId = null;
                    pass.Failures.Add((e, CalendarPlaces.CancelPost, error.Length <= 1024 ? error : error[..1023] + "…"));
                }

                continue;
            }

            date.CancelPostId = outcome.MessageId ?? string.Empty;
            pass.Written++;
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────

    private async Task WriteFactAsync(string type, CalendarEvent e, JsonObject data, DateTimeOffset now, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = e.Id.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>The instance's join link, while the occurrence Modbot opened is still open.</summary>
    private async Task<string?> JoinLinkAsync(CalendarEvent e, CancellationToken ct)
    {
        if (e.State != CalendarEventStates.Open || e.OccurrenceStartsAt is not { } occurrence)
            return null;

        var opening = await _db.CalendarOpenings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == e.Id && o.OccurrenceStartsAt == occurrence && o.Location != null, ct)
            .ConfigureAwait(false);

        if (opening?.Location is not { } location)
            return null;

        if (opening.InstanceId is { } instanceId
            && await _db.VRChatInstances.AsNoTracking().AnyAsync(i => i.Id == instanceId && i.ClosedAt != null, ct).ConfigureAwait(false))
        {
            return null;
        }

        return InstanceJoinLink.For(location);
    }

    private CalendarEventPlace AddPlace(CalendarEvent e, string place)
    {
        var row = new CalendarEventPlace
        {
            EventId = e.Id,
            Place = place,
            State = CalendarPlaceStates.Waiting,
            UpdatedAt = _clock.UtcNow,
        };

        _db.CalendarEventPlaces.Add(row);
        _added.Add(row);
        return row;
    }

    private static void Published(CalendarEventPlace place, string fingerprint, DateTimeOffset now)
    {
        place.State = CalendarPlaceStates.Published;
        place.SentFingerprint = fingerprint;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.UpdatedAt = now;
    }

    private static void Forget(CalendarEventPlace place, string state, DateTimeOffset now)
    {
        place.ExternalId = null;
        place.SentFingerprint = null;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.State = state;
        place.UpdatedAt = now;
    }

    private void Fail(Pass pass, CalendarEvent e, CalendarEventPlace place, DiscordPostOutcome outcome, string? fingerprint)
    {
        var error = outcome.Error ?? "Discord refused.";
        var repeated = place.State == CalendarPlaceStates.Failed && place.Error == error;

        place.State = CalendarPlaceStates.Failed;
        place.Error = error.Length <= 1024 ? error : error[..1023] + "…";
        place.ErrorAt = pass.Now;
        place.FailedFingerprint = outcome.Permanent ? fingerprint : null;
        place.UpdatedAt = pass.Now;

        // One fact per new problem, not one every twenty seconds while it lasts.
        if (!repeated)
            pass.Failures.Add((e, place.Place, place.Error));

        _log.Warning("Could not update the {Place} for the event {EventId}: {Reason}", place.Place, e.Id, error);
    }

    private sealed class Pass(
        IDiscordGateway gateway,
        string? guildId,
        string? publicAddress,
        DateTimeOffset now,
        IReadOnlyDictionary<string, VRChatWorld> worlds,
        CardStyle style,
        bool showPictures,
        CardPictures pictures,
        CancellationToken ct)
    {
        public string? GuildId { get; } = guildId;

        public string? PublicAddress { get; } = publicAddress;

        public CardStyle Style { get; } = style;

        /// <summary>A fresh set of files for one message.</summary>
        public CardPictureMessage Pictures() => pictures.ForMessage(showPictures);

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Ct { get; } = ct;

        public int Calls { get; private set; }

        public int Written { get; set; }

        public List<(CalendarEvent Event, string Place, string Error)> Failures { get; } = [];

        /// <summary>Events whose cancel post this pass dealt with, for the facts.</summary>
        public List<CalendarEvent> CancelledEvents { get; } = [];

        public VRChatWorld? WorldOf(CalendarEvent e) =>
            e.WorldId is { } id && worlds.TryGetValue(id, out var world) ? world : null;

        public Task<DiscordPostOutcome> Call(Func<IDiscordGateway, Task<DiscordPostOutcome>> call)
        {
            Calls++;
            return call(gateway);
        }
    }
}
