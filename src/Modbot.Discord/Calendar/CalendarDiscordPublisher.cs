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
/// <para>
/// <strong>Modbot's own posts come down a day after their event or date was due to end</strong>
/// (<see cref="PostKeptFor"/>, added 2026-10-01): the cards, kept in <see cref="CalendarOldPost"/>
/// once they have their last word, and both kinds of "Cancelled" line. Before, nothing ever removed
/// them, and a busy channel filled with old cards. Removing is the last thing a pass does, with
/// what is left of its calls and at most <see cref="RemovalsPerPass"/>, so posting and editing come
/// first and a long list -- an install that had cancel lines before this -- is worked through
/// slowly. Each removal is written on its own row and nowhere else: a fact for every old card would
/// bury the log the calendar's real changes are in.
/// </para>
/// </remarks>
public sealed class CalendarDiscordPublisher
{
    /// <summary>How many Discord calls one pass may make.</summary>
    public const int CallsPerPass = 5;

    /// <summary>How long after its event or date was due to end a post Modbot made in the channel stays up.</summary>
    public static readonly TimeSpan PostKeptFor = TimeSpan.FromDays(1);

    /// <summary>How many old posts one pass may take down, at most, out of <see cref="CallsPerPass"/>.</summary>
    public const int RemovalsPerPass = 2;

    /// <summary>How long a post Discord refused to let the bot delete is left alone before it is tried again.</summary>
    public static readonly TimeSpan RefusedPostRetryAfter = TimeSpan.FromDays(1);

    /// <summary>The reason Discord's audit log shows for an old post Modbot took down.</summary>
    public const string OldPostReason = "Calendar event is over";

    /// <summary>Discord refuses a server event that starts in the past; one that is late starts this far ahead.</summary>
    public static readonly TimeSpan LateStartAhead = TimeSpan.FromMinutes(1);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly CardPictures _pictures;
    private readonly OldPostRefusals _refused;
    private readonly ILogger _log;

    /// <summary>Places this pass made, for the state-change facts.</summary>
    private readonly List<CalendarEventPlace> _added = [];

    /// <summary>
    /// Places that were taken down and are now wanted again. A place is one row per event and
    /// kind of place, so the row left behind is reused rather than a second one made.
    /// </summary>
    private readonly Dictionary<(Guid EventId, string Place), CalendarEventPlace> _removed = [];

    public CalendarDiscordPublisher(
        ModbotContext db,
        IModbotClock clock,
        DiscordBotStatus status,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        CardPictures? pictures = null,
        OldPostRefusals? refused = null,
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
        _refused = refused ?? new OldPostRefusals();
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

        var oldPosts = await OldPostsDueAsync(now, ct).ConfigureAwait(false);

        var withPlaces = places.Select(p => p.EventId).Distinct().ToList();

        var events = await _db.CalendarEvents
            .Where(e => withPlaces.Contains(e.Id)
                || ((e.PublishToDiscord || e.PostToChannel)
                    && e.DeletedAt == null
                    && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)))
            .OrderBy(e => e.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (events.Count == 0 && cancelPosts.Count == 0 && dateCancelPosts.Count == 0 && oldPosts.Count == 0)
            return new CalendarDiscordPass(0);

        var eventIds = events.Select(e => e.Id).ToList();
        var takenDown = await _db.CalendarEventPlaces
            .Where(p => eventIds.Contains(p.EventId)
                && (p.Place == CalendarPlaces.DiscordEvent || p.Place == CalendarPlaces.ChannelPost)
                && p.State == CalendarPlaceStates.Removed)
            .ToListAsync(ct).ConfigureAwait(false);

        _removed.Clear();
        foreach (var row in takenDown)
            _removed[(row.EventId, row.Place)] = row;

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

        // Which dates have pinged their role already: every date, not only the latest, so a post
        // turned off and on, or an event moved to another date and back, does not ping one twice.
        var withRole = events.Where(e => e.MentionRoleId != null).Select(e => e.Id).ToList();

        if (withRole.Count > 0)
        {
            var mentioned = await _db.CalendarRolePings.AsNoTracking()
                .Where(p => withRole.Contains(p.EventId))
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var m in mentioned)
                pass.Mentioned.Add((m.EventId, m.StartsAt));
        }

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

        // Last, with what is left: taking an old post down is never more urgent than a change.
        await RemoveOldPostsAsync(pass, oldPosts).ConfigureAwait(false);

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
        {
            // Discord has this already: a failure since, cleared by an edit back or by Try again,
            // is over rather than left waiting for a write that never comes.
            if (place.State != CalendarPlaceStates.Published)
                Published(place, fingerprint, pass.Now);

            return;
        }

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

        // The role above the card, kept on every edit so the post goes on showing it. Only the
        // date's first post pings it (§3.3.1); an edit never does.
        var mention = RoleMention(e, pass.GuildId);

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
                var occurrence = place.OccurrenceStartsAt is { } was ? DateOf(e, was) : current;

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
                    .Call(g => g.EditAsync(postedIn, messageId, mention, [card], [], pictures: null, pass.Ct))
                    .ConfigureAwait(false);

                if (!edited.Sent && !edited.Permanent)
                {
                    Fail(pass, e, place, edited, fingerprint: null);
                    return;
                }

                // The place forgets the card now, and a repeating event's place moves on to the next
                // date, so the card is kept here until it comes down a day after its date ended. A
                // card Discord no longer has is not kept.
                if (edited.Sent)
                {
                    _db.CalendarOldPosts.Add(new CalendarOldPost
                    {
                        Id = Guid.CreateVersion7(pass.Now),
                        EventId = e.Id,
                        ChannelId = postedIn,
                        MessageId = messageId,
                        EndsAt = occurrence.EndsAt,
                    });
                }
                else
                {
                    _log.Information(
                        "The channel post for the event {EventId} could not be given its last word: {Reason}",
                        e.Id, edited.Error);
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

        object?[] said =
        [
            "channelPost", channelId, embed.Title, embed.Description, embed.Color, embed.Url, embed.Footer, embed.ImageUrl,
            string.Join('\n', embed.Fields.Select(f => f.Name + "=" + f.Value)), links.Count > 0 ? links[0].Url : null,
        ];

        // Added only with a role, so a post without one keeps the fingerprint it was sent under and
        // is not edited again for nothing.
        var fingerprint = CalendarFingerprint.Of(mention is null ? said : [.. said, mention]);

        if (place.ExternalId is not null && place.SentFingerprint == fingerprint)
        {
            // As for the Discord event: already there, so no failure or wait is left.
            if (place.State != CalendarPlaceStates.Published)
                Published(place, fingerprint, pass.Now);

            return;
        }

        if (place.State == CalendarPlaceStates.Failed && place.FailedFingerprint == fingerprint)
            return;

        DiscordPostOutcome outcome;

        if (first)
        {
            // Pinged once per date: a post made again for a date that has pinged shows the role and
            // pings nobody.
            var date = current.PlannedStartsAt;
            var ping = mention is not null && !pass.Mentioned.Contains((e.Id, date));

            outcome = ping
                ? await pass
                    .Call(g => g.PostMentioningRoleAsync(channelId!, e.MentionRoleId!.Trim(), [embed], links, pictures.Files, pass.Ct))
                    .ConfigureAwait(false)
                : await pass
                    .Call(g => g.PostAsync(channelId!, mention, [embed], links, pictures.Files, pass.Ct))
                    .ConfigureAwait(false);

            if (outcome is { Sent: true, MessageId: { } posted })
            {
                place.ExternalId = posted;
                place.ChannelId = channelId;
                place.OccurrenceStartsAt = date;

                if (ping)
                {
                    _db.CalendarRolePings.Add(new CalendarRolePing { EventId = e.Id, StartsAt = date, PingedAt = pass.Now });
                    pass.Mentioned.Add((e.Id, date));
                }
            }
        }
        else
        {
            var id = place.ExternalId!;
            var inChannel = place.ChannelId ?? channelId!;
            outcome = await pass
                .Call(g => g.EditAsync(inChannel, id, mention, [embed], links, pictures: null, pass.Ct))
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

    /// <summary>
    /// The line above the channel post's card: a mention of the event's role, or null for none
    /// (calendar design §3.3.1). Never the server's @everyone role, whose id is the server's.
    /// </summary>
    public static string? RoleMention(CalendarEvent calendarEvent, string? guildId)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var role = calendarEvent.MentionRoleId?.Trim();

        if (string.IsNullOrEmpty(role) || string.Equals(role, guildId?.Trim(), StringComparison.Ordinal))
            return null;

        return $"<@&{role}>";
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
    /// How long after a cancelled date ended its cancel post is still worth posting: as long as a
    /// posted one stays up (<see cref="PostKeptFor"/>), so a late one is not posted only to be taken
    /// down on the next pass.
    /// </summary>
    public static readonly TimeSpan DateCancelPostKeptFor = PostKeptFor;

    /// <summary>
    /// The cancel post for one date cancelled on its own, when the moderator ticked it: the same
    /// message a whole-event cancel posts, with that date's time, posted once. A refusal is not
    /// sent again; anything else is tried on the next pass.
    /// </summary>
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

    // ── Old posts ────────────────────────────────────────────────────────────────────────

    /// <summary>One post Modbot made in a channel that is due to come down, and how to mark it done.</summary>
    private sealed record OldPost(string ChannelId, string MessageId, DateTimeOffset EndsAt, Action<DateTimeOffset> Removed);

    /// <summary>
    /// The posts whose event or date was due to end more than <see cref="PostKeptFor"/> ago and are
    /// still up, oldest first, at most <see cref="RemovalsPerPass"/>: cards that had their last word,
    /// whole-event "Cancelled" lines and one-date "Cancelled" lines.
    /// </summary>
    /// <remarks>
    /// Only rows that hold a message id Modbot was given when it posted, so nothing anyone else
    /// posted is ever touched. A line Discord gave no id for cannot be found again, and stays.
    /// </remarks>
    private async Task<List<OldPost>> OldPostsDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = now - PostKeptFor;

        // Posts Discord refused to let the bot delete are left out here, not skipped later, so a
        // refused one at the front of the line does not take the pass's place from the rest.
        var held = _refused.OnHold(now);

        var cards = await _db.CalendarOldPosts
            .Where(p => p.RemovedAt == null && p.EndsAt <= due && !held.Contains(p.MessageId))
            .OrderBy(p => p.EndsAt)
            .Take(RemovalsPerPass)
            .ToListAsync(ct).ConfigureAwait(false);

        // A date ends after it starts, so a start past the line is the first sift; the end, which
        // can be moved or follow the event's length, is worked out below.
        var lines = await _db.CalendarEventPlaces
            .Where(p => p.Place == CalendarPlaces.CancelPost
                && p.State == CalendarPlaceStates.Published
                && p.ExternalId != null && p.ExternalId != string.Empty && !held.Contains(p.ExternalId)
                && p.ChannelId != null && p.ChannelId != string.Empty
                && p.OccurrenceStartsAt != null && p.OccurrenceStartsAt <= due)
            .ToListAsync(ct).ConfigureAwait(false);

        var dateLines = await _db.CalendarDateChanges
            .Where(c => c.CancelPostRemovedAt == null
                && c.CancelPostId != null && c.CancelPostId != string.Empty && !held.Contains(c.CancelPostId)
                && c.CancelPostChannelId != null && c.CancelPostChannelId != string.Empty
                && (c.StartsAt ?? c.PlannedStartsAt) <= due)
            .ToListAsync(ct).ConfigureAwait(false);

        var posts = cards
            .Select(p => new OldPost(p.ChannelId, p.MessageId, p.EndsAt, at => p.RemovedAt = at))
            .ToList();

        if (lines.Count > 0 || dateLines.Count > 0)
        {
            var ids = lines.Select(p => p.EventId).Concat(dateLines.Select(c => c.EventId)).Distinct().ToList();
            var owners = await _db.CalendarEvents
                .Where(e => ids.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

            foreach (var line in lines)
            {
                if (!owners.TryGetValue(line.EventId, out var e))
                    continue;

                // The cancel keeps the date it named by its start as it was then, moves included.
                var startsAt = line.OccurrenceStartsAt!.Value;
                var endsAt = (CalendarRepeat.StartingAt(e, startsAt)
                    ?? new CalendarOccurrence(startsAt, startsAt + CalendarRepeat.LengthOf(e))).EndsAt;

                posts.Add(new OldPost(line.ChannelId!, line.ExternalId!, endsAt, at =>
                {
                    line.State = CalendarPlaceStates.Removed;
                    line.UpdatedAt = at;
                }));
            }

            foreach (var date in dateLines)
            {
                if (!owners.TryGetValue(date.EventId, out var e))
                    continue;

                posts.Add(new OldPost(
                    date.CancelPostChannelId!,
                    date.CancelPostId!,
                    CalendarRepeat.Changed(date, CalendarRepeat.LengthOf(e)).EndsAt,
                    at => date.CancelPostRemovedAt = at));
            }
        }

        return posts
            .Where(p => p.EndsAt <= due)
            .OrderBy(p => p.EndsAt)
            .Take(RemovalsPerPass)
            .ToList();
    }

    /// <summary>
    /// Deletes each old post from Discord with what is left of the pass's calls. A post that is gone
    /// already -- deleted by hand, Discord's 404 -- counts as done and is not asked about again. A
    /// refusal to let the bot delete it (403, a role or permission taken away) is not done: it
    /// says so in the log and is tried again after <see cref="RefusedPostRetryAfter"/>, in case
    /// the permission comes back. Anything else is tried on the next pass.
    /// </summary>
    private async Task RemoveOldPostsAsync(Pass pass, List<OldPost> posts)
    {
        foreach (var post in posts)
        {
            if (pass.Calls >= CallsPerPass)
                return;

            var deleted = await pass
                .Call(g => g.DeleteMessageAsync(post.ChannelId, post.MessageId, OldPostReason, pass.Ct))
                .ConfigureAwait(false);

            if (deleted.Sent || deleted.NotFound)
            {
                post.Removed(pass.Now);
                _refused.Forget(post.MessageId);

                if (!deleted.Sent)
                {
                    _log.Information(
                        "An old calendar post was already gone, and will not be asked about again: {Reason}",
                        deleted.Error);
                }

                continue;
            }

            if (deleted.Permanent)
            {
                _refused.Hold(post.MessageId, pass.Now + RefusedPostRetryAfter);
                _log.Warning(
                    "Discord would not let the bot take down an old calendar post; it will be tried again in a day: {Reason}",
                    deleted.Error);
                continue;
            }

            _log.Warning("Could not take down an old calendar post; it will be tried again: {Reason}", deleted.Error);
        }
    }

    /// <summary>
    /// The date a post was made for, known by its planned start, at the times it has now: its own
    /// when it was moved on its own (§2.2), the event's length otherwise. A cancelled date keeps the
    /// times it had.
    /// </summary>
    private static CalendarOccurrence DateOf(CalendarEvent e, DateTimeOffset plannedStartsAt) =>
        e.DateChanges.FirstOrDefault(c => c.PlannedStartsAt == plannedStartsAt) is { } change
            ? CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(e))
            : new CalendarOccurrence(plannedStartsAt, plannedStartsAt + CalendarRepeat.LengthOf(e));

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
        // Turned off and on again: the row from before is wanted again, from the start. Making a
        // second one broke the key (event, place) and stopped the whole pass saving.
        if (_removed.Remove((e.Id, place), out var again))
        {
            Forget(again, CalendarPlaceStates.Waiting, _clock.UtcNow);
            again.ChannelId = null;
            again.OccurrenceStartsAt = null;
            _added.Add(again);
            return again;
        }

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

        /// <summary>Each event's dates whose post has pinged its role, from every channel post row it has had.</summary>
        public HashSet<(Guid EventId, DateTimeOffset Date)> Mentioned { get; } = [];

        public Task<DiscordPostOutcome> Call(Func<IDiscordGateway, Task<DiscordPostOutcome>> call)
        {
            Calls++;
            return call(gateway);
        }
    }
}
