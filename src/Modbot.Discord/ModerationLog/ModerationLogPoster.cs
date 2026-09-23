using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.ModerationLog;

public enum ModerationLogPassOutcome
{
    /// <summary>No enabled route sends anywhere. Nothing read.</summary>
    NoChannel = 1,

    /// <summary>A channel was just turned on: its place moved to the newest fact and nothing was posted.</summary>
    StartedFromNow = 2,

    /// <summary>Nothing new since the channel's place, or nothing new that its routes take.</summary>
    NothingNew = 3,

    /// <summary>At least one message went out.</summary>
    Posted = 4,

    /// <summary>The first message was refused. The channel's place did not move.</summary>
    Failed = 5,

    /// <summary>The channel was refused a short while ago and is not tried again yet.</summary>
    Waiting = 6,
}

/// <param name="ChannelId">The channel this part of the pass was for.</param>
/// <param name="Read">Facts read past the channel's place, of every type.</param>
/// <param name="Posted">Events that reached the channel.</param>
/// <param name="Error">Why posting stopped, when it did.</param>
public sealed record ModerationLogChannelPass(
    string ChannelId, ModerationLogPassOutcome Outcome, int Read, int Posted, string? Error);

/// <summary>One pass over every routed channel.</summary>
/// <param name="Outcome">
/// The pass as a whole: <see cref="ModerationLogPassOutcome.Posted"/> when anything went out,
/// otherwise <see cref="ModerationLogPassOutcome.Failed"/> when a channel refused, otherwise
/// whatever the channels agree on.
/// </param>
/// <param name="Read">Facts read, summed over channels.</param>
/// <param name="Posted">Events posted, summed over channels.</param>
/// <param name="Error">The first refusal of the pass, if any.</param>
public sealed record ModerationLogPass(
    ModerationLogPassOutcome Outcome,
    int Read,
    int Posted,
    string? Error,
    IReadOnlyList<ModerationLogChannelPass> Channels);

/// <summary>
/// Reads new facts from the log and posts each one to the Discord channels whose routes take it,
/// one pass at a time (Discord event routes design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each channel keeps its own place</strong>, a fact id in <c>discord_event_channel</c>:
/// "every row after this one" is an index range on the log's primary key. Facts are read in id
/// order regardless of type and the place moves past all of them, so a busy night of instance
/// joins does not leave a channel re-reading the same page. One place per channel rather than one
/// for all, so a channel that lost its permissions waits on its own.
/// </para>
/// <para>
/// <strong>Turning a channel on posts nothing.</strong> A channel with no row has not started; it
/// jumps to the newest fact. A channel no enabled route sends to loses its row, so turning it back
/// on starts from then too. Replaying months of bans into a channel the moment somebody picks it is
/// what every operator expects to be protected from.
/// </para>
/// <para>
/// <strong>Some types never leave the building.</strong> Every fact is checked against
/// <see cref="DiscordEventTypes.CanSend"/> as well as against the route, so sign-ins and reset links
/// never reach Discord whatever a route row says.
/// </para>
/// <para>
/// <strong>Pacing.</strong> A backlog goes out ten events to a message, a second and a bit apart,
/// at most a handful of messages per channel per pass. A refused post stops that channel with its
/// place at the last event that did go out, so nothing is skipped and nothing is repeated, and the
/// channel is left alone for a while before it is tried again.
/// </para>
/// </remarks>
public sealed class ModerationLogPoster
{
    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly ModerationLogOptions _options;
    private readonly CardPictures _pictures;
    private readonly ILogger _log;

    public ModerationLogPoster(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        DiscordBotStatus status,
        ModerationLogOptions? options = null,
        CardPictures? pictures = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(status);

        _db = db;
        _facts = facts;
        _clock = clock;
        _status = status;
        _options = options ?? new ModerationLogOptions();
        _pictures = pictures ?? new CardPictures();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<ModerationLogPass> RunOnceAsync(
        IDiscordGateway gateway,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(delay);

        var routes = await _db.DiscordEventRoutes.AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Position)
            .ThenBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // One entry per channel, in the order its first route is listed.
        var channels = routes
            .Where(r => !string.IsNullOrWhiteSpace(r.ChannelId))
            .GroupBy(r => r.ChannelId.Trim(), StringComparer.Ordinal)
            .Select(g => (ChannelId: g.Key, Routes: g.ToList()))
            .ToList();

        var places = await _db.DiscordEventChannels.ToListAsync(ct).ConfigureAwait(false);
        var wanted = channels.Select(c => c.ChannelId).ToHashSet(StringComparer.Ordinal);

        // Turned off or deleted: forget where it was, so turning it back on starts from then.
        var forgotten = places.Where(p => !wanted.Contains(p.ChannelId)).ToList();
        if (forgotten.Count > 0)
        {
            _db.DiscordEventChannels.RemoveRange(forgotten);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (channels.Count == 0)
            return new ModerationLogPass(ModerationLogPassOutcome.NoChannel, 0, 0, null, []);

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.PublicAddress, s.ManagedGroupName, s.VRChatImagesProxied })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // What every card in this pass shares. The mark beside the footer is Modbot's own, which
        // costs nothing to link because it is served from the public address; the footer names the
        // group, so a server watching more than one Modbot can tell the channels apart.
        var style = new CardStyle(
            settings?.PublicAddress,
            settings?.ManagedGroupName,
            BrandIcon.For(settings?.PublicAddress));

        var results = new List<ModerationLogChannelPass>();
        long? newest = null;

        foreach (var (channelId, channelRoutes) in channels)
        {
            var place = places.FirstOrDefault(p => string.Equals(p.ChannelId, channelId, StringComparison.Ordinal));

            if (place is null)
            {
                // An empty log has no newest fact; zero -- everything after fact 0 -- is then right.
                newest ??= await _db.Events.AsNoTracking().MaxAsync(e => (long?)e.Id, ct).ConfigureAwait(false) ?? 0;

                _db.DiscordEventChannels.Add(new DiscordEventChannel { ChannelId = channelId, PostedThrough = newest.Value });
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Information(
                    "Discord channel {Channel} turned on; posting events from now on (after fact #{Id}) and none of the history before it",
                    channelId, newest.Value);

                results.Add(new ModerationLogChannelPass(channelId, ModerationLogPassOutcome.StartedFromNow, 0, 0, null));
                continue;
            }

            if (place.RetryAt is { } retryAt && retryAt > _clock.UtcNow)
            {
                results.Add(new ModerationLogChannelPass(channelId, ModerationLogPassOutcome.Waiting, 0, 0, place.LastError));
                continue;
            }

            results.Add(await PostChannelAsync(
                    gateway, delay, place, channelRoutes, style, settings?.VRChatImagesProxied ?? true, ct)
                .ConfigureAwait(false));
        }

        var posted = results.Sum(r => r.Posted);
        var failed = results.FirstOrDefault(r => r.Outcome == ModerationLogPassOutcome.Failed || r.Error is not null);

        var outcome = posted > 0 ? ModerationLogPassOutcome.Posted
            : results.Any(r => r.Outcome == ModerationLogPassOutcome.Failed) ? ModerationLogPassOutcome.Failed
            : results.Any(r => r.Outcome == ModerationLogPassOutcome.StartedFromNow) ? ModerationLogPassOutcome.StartedFromNow
            : results.All(r => r.Outcome == ModerationLogPassOutcome.Waiting) ? ModerationLogPassOutcome.Waiting
            : ModerationLogPassOutcome.NothingNew;

        return new ModerationLogPass(outcome, results.Sum(r => r.Read), posted, failed?.Error, results);
    }

    private async Task<ModerationLogChannelPass> PostChannelAsync(
        IDiscordGateway gateway,
        Func<TimeSpan, CancellationToken, Task> delay,
        DiscordEventChannel place,
        IReadOnlyList<DiscordEventRoute> routes,
        CardStyle style,
        bool showPictures,
        CancellationToken ct)
    {
        var channelId = place.ChannelId;
        var cursor = place.PostedThrough;

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.Id > cursor)
            .OrderBy(e => e.Id)
            .Take(_options.FactsPerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return new ModerationLogChannelPass(channelId, ModerationLogPassOutcome.NothingNew, 0, 0, null);

        var types = routes.SelectMany(r => r.EventTypes).ToHashSet(StringComparer.Ordinal);
        var candidates = rows.Where(r => types.Contains(r.Type) && DiscordEventTypes.CanSend(r.Type)).ToList();

        var people = candidates.Count > 0 && routes.Any(r => r.HasPeopleFilters)
            ? await RoutePeople.LoadAsync(
                    _db,
                    candidates,
                    withRoles: routes.Any(r => r.SubjectVRChatRoleIds.Count > 0 || r.ActorVRChatRoleIds.Count > 0 || r.ActorModbotRoleIds.Count > 0),
                    ct)
                .ConfigureAwait(false)
            : RoutePeople.Empty;

        var matching = candidates.Where(f => EventRouteMatch.AnyMatches(routes, f, people)).ToList();

        if (matching.Count == 0)
        {
            place.PostedThrough = rows[^1].Id;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new ModerationLogChannelPass(channelId, ModerationLogPassOutcome.NothingNew, rows.Count, 0, null);
        }

        var names = await DisplayNames.LoadAsync(
                _db, matching.Select(m => m.SubjectId).Concat(matching.Select(m => m.ActorId)), ct)
            .ConfigureAwait(false);

        // The worlds the instance events happened in, so a warn or an instance kick can say where
        // by name. Only the events that carry a world are asked for, and a world Modbot has never
        // read simply has no name, which leaves that card without the field.
        var worlds = await WorldNames.LoadAsync(_db, matching.Select(m => m.WorldId), ct).ConfigureAwait(false);

        // Only the people a card is headed by, so a pass does not read pictures for the actors,
        // whose names sit in a field and carry no picture.
        var faces = showPictures
            ? await PersonPictures.LoadAsync(_db, matching.Select(m => m.SubjectId), ct).ConfigureAwait(false)
            : [];

        var posted = 0;
        var postedThrough = cursor;
        var messages = 0;
        string? error = null;

        foreach (var chunk in matching.Chunk(_options.EmbedsPerMessage))
        {
            if (messages >= _options.MessagesPerPass)
                break;

            if (messages > 0)
                await delay(_options.GapBetweenMessages, ct).ConfigureAwait(false);

            // The faces are collected per message, because Discord counts files by the message and
            // two cards about the same person then cost one upload rather than two.
            var pictures = _pictures.ForMessage(showPictures);
            var cards = new List<DiscordEmbedContent>(chunk.Length);

            foreach (var fact in chunk)
            {
                var view = ModerationEventView.From(fact, names, worlds);
                var face = faces.GetValueOrDefault(view.SubjectId);

                cards.Add(EventCard.For(
                    view,
                    style,
                    new CardPicture(AuthorIcon: await pictures.AddAsync(face, ct).ConfigureAwait(false))));
            }

            var outcome = await gateway
                .PostAsync(channelId, null, cards, null, pictures.Files, ct)
                .ConfigureAwait(false);
            messages++;

            if (!outcome.Sent)
            {
                error = outcome.Error ?? "Discord refused the message.";
                break;
            }

            posted += chunk.Length;
            postedThrough = chunk[^1].Id;
        }

        var now = _clock.UtcNow;

        if (error is not null)
        {
            place.LastError = error;
            place.LastErrorAt = now;
            place.RetryAt = now + _options.RetryAfterFailure;
            _status.Problem(error, now);
            _log.Warning("Could not post to Discord channel {Channel}: {Reason}", channelId, error);
        }

        if (posted == 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new ModerationLogChannelPass(channelId, ModerationLogPassOutcome.Failed, rows.Count, 0, error);
        }

        // Everything that matched went out, so the trailing facts it did not take are read too.
        if (postedThrough == matching[^1].Id)
            postedThrough = rows[^1].Id;

        place.PostedThrough = postedThrough;
        place.LastPostedAt = now;

        if (error is null)
        {
            place.LastError = null;
            place.LastErrorAt = null;
            place.RetryAt = null;
        }

        await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.DiscordLogPosted,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = channelId,
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["count"] = posted,
                    ["fromId"] = matching[0].Id,
                    ["toId"] = postedThrough,
                },
            }, ct)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _status.Posted(posted, now);

        return new ModerationLogChannelPass(channelId, ModerationLogPassOutcome.Posted, rows.Count, posted, error);
    }
}
