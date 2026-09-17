using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
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
    private readonly ILogger _log;

    public CalendarDiscordPublisher(
        ModbotContext db,
        IModbotClock clock,
        DiscordBotStatus status,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
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

        var withPlaces = places.Select(p => p.EventId).Distinct().ToList();

        var events = await _db.CalendarEvents
            .Where(e => withPlaces.Contains(e.Id)
                || ((e.PublishToDiscord || e.PostToChannel)
                    && e.DeletedAt == null
                    && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)))
            .OrderBy(e => e.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (events.Count == 0)
            return new CalendarDiscordPass(0);

        var worldIds = events.Where(e => e.WorldId != null).Select(e => e.WorldId!).Distinct().ToList();
        var worlds = await _db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var pass = new Pass(gateway, guildId, publicAddress, now, worlds, ct);

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
            await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
            await _facts.WriteAsync(
                new FactRecord
                {
                    Type = FactType.PlannedEventPublishFailed,
                    OccurredAt = now,
                    SubjectPlatform = FactPlatform.Modbot,
                    SubjectId = calendarEvent.Id.ToString(),
                    Source = FactSource.Modbot,
                    Data = new JsonObject { ["title"] = calendarEvent.Title, ["place"] = place, ["error"] = error },
                },
                ct).ConfigureAwait(false);
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

        // The occurrence moved on, or the event is not wanted in Discord any more: end the one there is.
        if (place?.ExternalId is { } existing && (!wants || place.OccurrenceStartsAt != e.OccurrenceStartsAt))
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

        var occurrence = Occurrence(e);
        var open = e.State == CalendarEventStates.Open;
        var world = pass.WorldOf(e);
        var location = open ? ShortJoinAddress(pass.PublicAddress, e) ?? joinLink : null;

        var fingerprint = CalendarFingerprint.Of(
            "discordEvent", e.Title, e.Description, occurrence.StartsAt, occurrence.EndsAt,
            location, joinLink, open, e.ImageUrl, world?.Name, world?.ImageUrl, e.WorldId);

        if (place.ExternalId is not null && place.SentFingerprint == fingerprint)
            return;

        if (place.State == CalendarPlaceStates.Failed && place.FailedFingerprint == fingerprint)
            return;

        var startsAt = occurrence.StartsAt > pass.Now ? occurrence.StartsAt : pass.Now + LateStartAhead;
        var details = CalendarCard.EventDetails(e, world, startsAt, occurrence.EndsAt, location, open ? joinLink : null);
        var guildId = pass.GuildId!;

        DiscordPostOutcome outcome;

        if (place.ExternalId is null)
        {
            outcome = await pass.Call(g => g.CreateEventAsync(guildId, details, pass.Ct)).ConfigureAwait(false);

            if (outcome is { Sent: true, MessageId: { } created })
            {
                place.ExternalId = created;
                place.ChannelId = guildId;
                place.OccurrenceStartsAt = e.OccurrenceStartsAt;

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

        if (place?.ExternalId is { } messageId && place.ChannelId is { } postedIn)
        {
            var movedOn = CalendarEventStates.IsLive(e.State) && place.OccurrenceStartsAt != e.OccurrenceStartsAt;
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
                // The post's last word, about the occurrence it was made for.
                var state = removedInModbot ? CalendarCardState.Cancelled : CalendarCardState.Finished;
                var occurrence = place.OccurrenceStartsAt is { } was
                    ? new CalendarOccurrence(was, was + (e.EndsAt - e.StartsAt))
                    : Occurrence(e);

                var card = CalendarCard.For(e, occurrence, pass.WorldOf(e), state, joinLink: null);
                var edited = await pass.Call(g => g.EditAsync(postedIn, messageId, null, [card], [], pass.Ct)).ConfigureAwait(false);

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

        var current = Occurrence(e);
        var open = e.State == CalendarEventStates.Open;
        var cardState = open ? CalendarCardState.Open : CalendarCardState.Scheduled;
        var embed = CalendarCard.For(e, current, pass.WorldOf(e), cardState, joinLink);
        var links = CalendarCard.Links(cardState, joinLink);

        var fingerprint = CalendarFingerprint.Of(
            "channelPost", channelId, embed.Title, embed.Description, embed.Color, embed.Url, embed.Footer, embed.ImageUrl,
            string.Join('\n', embed.Fields.Select(f => f.Name + "=" + f.Value)), links.Count > 0 ? links[0].Url : null);

        if (place.ExternalId is not null && place.SentFingerprint == fingerprint)
            return;

        if (place.State == CalendarPlaceStates.Failed && place.FailedFingerprint == fingerprint)
            return;

        DiscordPostOutcome outcome;

        if (place.ExternalId is null)
        {
            outcome = await pass.Call(g => g.PostAsync(channelId!, null, [embed], links, pass.Ct)).ConfigureAwait(false);

            if (outcome is { Sent: true, MessageId: { } posted })
            {
                place.ExternalId = posted;
                place.ChannelId = channelId;
                place.OccurrenceStartsAt = e.OccurrenceStartsAt;
            }
        }
        else
        {
            var id = place.ExternalId;
            var inChannel = place.ChannelId ?? channelId!;
            outcome = await pass.Call(g => g.EditAsync(inChannel, id, null, [embed], links, pass.Ct)).ConfigureAwait(false);

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

    // ── Shared ───────────────────────────────────────────────────────────────────────────

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

    private static CalendarOccurrence Occurrence(CalendarEvent e)
    {
        var starts = e.OccurrenceStartsAt ?? e.StartsAt;
        return new CalendarOccurrence(starts, starts + (e.EndsAt - e.StartsAt));
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
        CancellationToken ct)
    {
        public string? GuildId { get; } = guildId;

        public string? PublicAddress { get; } = publicAddress;

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Ct { get; } = ct;

        public int Calls { get; private set; }

        public int Written { get; set; }

        public List<(CalendarEvent Event, string Place, string Error)> Failures { get; } = [];

        public VRChatWorld? WorldOf(CalendarEvent e) =>
            e.WorldId is { } id && worlds.TryGetValue(id, out var world) ? world : null;

        public Task<DiscordPostOutcome> Call(Func<IDiscordGateway, Task<DiscordPostOutcome>> call)
        {
            Calls++;
            return call(gateway);
        }
    }
}
