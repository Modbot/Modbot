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
using Modbot.Discord.Interactions;
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
/// <para>
/// <strong>Repeats of one change share a post.</strong> The same change to the same thing by the
/// same person, inside an hour, is written into the post before it while that post is still the
/// channel's newest message, and its title says how many (Discord event repeats design). The
/// channel's row remembers that post, so a restart carries on with it.
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
        var candidates = rows
            .Where(r => types.Contains(DiscordEventTypes.RouteTypeOf(r.Type)) && DiscordEventTypes.CanSend(r.Type))
            .ToList();

        var people = candidates.Count > 0 && routes.Any(r => r.HasPeopleFilters)
            ? await RoutePeople.LoadAsync(
                    _db,
                    candidates,
                    withRoles: routes.Any(r => r.SubjectVRChatRoleIds.Count > 0 || r.ActorVRChatRoleIds.Count > 0 || r.ActorModbotRoleIds.Count > 0),
                    _clock.UtcNow,
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
        // A calendar event's own world, and its world before and after a change, are named in the
        // payload rather than in the column, and are read with the rest.
        var worlds = await WorldNames.LoadAsync(
                _db,
                matching.Select(m => m.WorldId).Concat(matching.SelectMany(ModerationEventView.WorldsNamedIn)),
                ct)
            .ConfigureAwait(false);

        // Only the people a card is headed by, so a pass does not read pictures for the actors,
        // whose names sit in a field and carry no picture.
        var faces = showPictures
            ? await PersonPictures.LoadAsync(
                    _db, matching.Where(m => m.SubjectPlatform != FactPlatform.Discord).Select(m => m.SubjectId), ct)
                .ConfigureAwait(false)
            : [];

        // The Discord accounts the events are about or were done by, by their own ids: the name the
        // server shows (from Modbot's member list, else its ban list) and the picture, for the cards
        // headed by a Discord member and for a Discord moderator in a By field.
        var discord = await DiscordPeople.LoadAsync(
                _db,
                matching.Where(m => m.SubjectPlatform == FactPlatform.Discord).Select(m => m.SubjectId)
                    .Concat(matching.Where(m => m.ActorPlatform == FactPlatform.Discord).Select(m => m.ActorId)),
                ct)
            .ConfigureAwait(false);

        // Who decided a ban, kick or unban that Modbot made, and why: VRChat's own entry for it
        // only says Modbot did it.
        var decisions = await ModbotDecisions.LoadAsync(_db, matching, ct).ConfigureAwait(false);

        var posted = 0;
        var postedThrough = cursor;
        var messages = 0;

        // Every post and edit sent, the refused ones too, for the gap between them: an edit counts
        // against the channel's rate limit as a post does.
        var sent = 0;
        string? error = null;

        // Repeats of one change, one after another, are one card (Discord event repeats design §2).
        // The first card may also carry on the post already at the bottom of the channel.
        var window = _options.RepeatWindow;
        var open = OpenPost.Of(place);
        var cards = new List<Repeats>();

        foreach (var fact in matching)
        {
            if (cards.Count > 0 && cards[^1].Takes(fact, window))
            {
                cards[^1].Add(fact);
                continue;
            }

            cards.Add(new Repeats(fact, cards.Count == 0 && open is not null && open.Takes(fact, window) ? open : null));
        }

        if (cards[0].Earlier is { } earlier
            && !await StillNewestAsync(gateway, channelId, earlier.MessageId, ct).ConfigureAwait(false))
        {
            cards[0].Earlier = null;
        }

        // The message the channel ends with once this pass is done: its id, and its card when it
        // has only one. Null while nothing has gone out.
        (string? MessageId, Repeats? Only)? newest = null;
        var next = 0;

        if (cards[0].Earlier is { } post)
        {
            var (embeds, files) = await DrawAsync([cards[0]], names, worlds, discord, decisions, faces, style, showPictures, ct)
                .ConfigureAwait(false);
            embeds[0] = EmbedSize.Shorten(embeds[0]);

            // The same buttons the post was made with (acting from Discord design §7): an edit sets a
            // message's buttons whole, and a repeat must not take away the ones a moderator acts
            // from. The card is about the same person, so they are the same buttons.
            var buttons = await ButtonsForAsync(cards[0].Latest, style, ct).ConfigureAwait(false);

            // The files are sent again rather than left as they were, so a person whose picture
            // changed since the first post is shown with the new one.
            var outcome = await gateway
                .EditAsync(
                    channelId,
                    post.MessageId,
                    null,
                    embeds,
                    buttons.Links.Count > 0 ? buttons.Links : null,
                    files,
                    buttons.Actions.Count > 0 ? buttons.Actions : null,
                    ct)
                .ConfigureAwait(false);
            sent++;

            if (outcome.Sent)
            {
                messages++;
                posted += cards[0].Count;
                postedThrough = cards[0].Latest.Id;
                newest = (post.MessageId, cards[0]);
                next = 1;
            }
            else if (outcome.Permanent || outcome.NotFound)
            {
                // Deleted by hand, or no longer the bot's to change: the repeats start a post of
                // their own, counted from this pass.
                _log.Information(
                    "The Discord post {Message} in channel {Channel} could not take a repeat ({Reason}); posting a new one",
                    post.MessageId, channelId, outcome.Error);
                cards[0].Earlier = null;
                OpenPost.Forget(place);
            }
            else
            {
                error = outcome.Error ?? "Discord refused the edit.";
            }
        }

        var toPost = error is null ? cards.Skip(next).ToList() : [];
        var at = 0;

        while (at < toPost.Count)
        {
            if (messages >= _options.MessagesPerPass)
                break;

            if (sent > 0)
                await delay(_options.GapBetweenMessages, ct).ConfigureAwait(false);

            // Up to the most cards a message holds, then fewer when their words add up to more than
            // Discord takes in one message. A card too big even alone is cut down, so the message
            // always goes out and the cards behind it are not held up by one that cannot.
            var chunk = toPost.GetRange(at, Math.Min(_options.EmbedsPerMessage, toPost.Count - at)).ToArray();
            var (embeds, files) = await DrawAsync(chunk, names, worlds, discord, decisions, faces, style, showPictures, ct)
                .ConfigureAwait(false);

            var fit = EmbedSize.HowManyFit(embeds);

            if (fit < chunk.Length)
            {
                chunk = chunk[..fit];
                (embeds, files) = await DrawAsync(chunk, names, worlds, discord, decisions, faces, style, showPictures, ct)
                    .ConfigureAwait(false);
            }

            if (chunk.Length == 1)
                embeds[0] = EmbedSize.Shorten(embeds[0]);

            at += chunk.Length;

            // Buttons only under a message that is one card: Discord puts them under the message,
            // and a row under ten cards could not say which one it acts on (acting from Discord
            // design §7). A quiet log posts one card at a time, which is when they matter.
            var buttons = chunk.Length == 1
                ? await ButtonsForAsync(chunk[0].Latest, style, ct).ConfigureAwait(false)
                : CardButtonSet.None;

            var outcome = await gateway
                .PostAsync(
                    channelId,
                    null,
                    embeds,
                    buttons.Links.Count > 0 ? buttons.Links : null,
                    files,
                    buttons.Actions.Count > 0 ? buttons.Actions : null,
                    ct)
                .ConfigureAwait(false);
            messages++;
            sent++;

            if (!outcome.Sent)
            {
                error = outcome.Error ?? "Discord refused the message.";
                break;
            }

            posted += chunk.Sum(c => c.Count);
            postedThrough = chunk[^1].Latest.Id;
            newest = (outcome.MessageId, chunk.Length == 1 ? chunk[0] : null);
        }

        if (newest is { } last)
        {
            if (last.MessageId is { } messageId
                && last.Only is { } only
                && DiscordEventTypes.FoldsRepeats(only.Latest.Type))
            {
                OpenPost.Remember(place, messageId, only);
            }
            else
            {
                OpenPost.Forget(place);
            }
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

    /// <summary>
    /// The buttons under a card that is a message of its own: <see cref="CardButtons"/>, with the
    /// case file that covers a ban when there is one and a public address to open it at.
    /// </summary>
    private async Task<CardButtonSet> ButtonsForAsync(ModbotEvent fact, CardStyle style, CancellationToken ct)
    {
        string? caseUrl = null;

        if (fact.SubjectPlatform == FactPlatform.VRChat
            && CardButtons.IsBan(fact.Type)
            && style.PublicAddress is { Length: > 0 } address)
        {
            // The case file this ban wrote, else the one covering their ban now.
            var caseId = await _db.CaseFiles.AsNoTracking()
                .Where(c => c.UserId == fact.SubjectId && c.WithdrawnAt == null && c.LiftedAt == null)
                .OrderByDescending(c => c.BanFactId == fact.Id)
                .ThenByDescending(c => c.CreatedAt)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (caseId is { } id)
                caseUrl = $"{address.TrimEnd('/')}/cases/{id}";
        }

        return CardButtons.For(fact.Type, fact.SubjectPlatform, fact.SubjectId, caseUrl);
    }

    /// <summary>The cards for one message, and the pictures they point at.</summary>
    /// <remarks>
    /// The faces are collected per message, because Discord counts files by the message and two
    /// cards about the same person then cost one upload rather than two.
    /// </remarks>
    private async Task<(List<DiscordEmbedContent> Embeds, IReadOnlyList<DiscordPicture> Files)> DrawAsync(
        IReadOnlyList<Repeats> cards,
        IReadOnlyDictionary<string, string?> names,
        IReadOnlyDictionary<string, string?> worlds,
        IReadOnlyDictionary<string, DiscordFace> discord,
        IReadOnlyDictionary<long, EventDecision> decisions,
        IReadOnlyDictionary<string, string?> faces,
        CardStyle style,
        bool showPictures,
        CancellationToken ct)
    {
        var pictures = _pictures.ForMessage(showPictures);
        var embeds = new List<DiscordEmbedContent>(cards.Count);
        var discordNames = discord.ToDictionary(d => d.Key, d => d.Value.Name, StringComparer.Ordinal);

        foreach (var card in cards)
        {
            var view = ModerationEventView.From(card.Latest, names, worlds, discordNames, decisions);

            // A Discord picture is Discord's own address, which Discord loads itself; a VRChat one
            // has to be fetched and sent with the message.
            var icon = view.OnDiscord
                ? discord.GetValueOrDefault(view.SubjectId)?.AvatarUrl
                : await pictures.AddAsync(faces.GetValueOrDefault(view.SubjectId), ct).ConfigureAwait(false);

            var embed = EventCard.For(view, style, new CardPicture(AuthorIcon: icon));

            embeds.Add(EventCard.Repeated(embed, card.Total, card.TotalLastAt - card.TotalFirstAt));
        }

        return (embeds, pictures.Files);
    }

    /// <summary>
    /// Whether the post is still the newest message in the channel, so that a repeat written into
    /// it is still read last. Anybody's message after it -- a person's, another bot's, another of
    /// Modbot's own features' -- means the repeat is posted on its own instead.
    /// </summary>
    /// <remarks>
    /// One request, made only when a repeat could go into the post. A channel that cannot be read
    /// counts as "something came after": a new post is never wrong, an edit out of order is. Discord
    /// answers a bot without Read Message History with no messages rather than a refusal; the edit
    /// itself then fails, and the repeat is posted on its own.
    /// </remarks>
    private async Task<bool> StillNewestAsync(
        IDiscordGateway gateway, string channelId, string messageId, CancellationToken ct)
    {
        var after = await gateway.ReadMessagesAsync(channelId, null, messageId, ct).ConfigureAwait(false);

        if (after.Error is not null)
        {
            _log.Debug(
                "Could not check whether post {Message} is still the newest in channel {Channel}: {Reason}",
                messageId, channelId, after.Error);
            return false;
        }

        return after.Messages.Count == 0 && after.NewestId is null;
    }

    /// <summary>
    /// One card's worth of events: one event, or a run of repeats of one change, perhaps going on
    /// from the post already at the bottom of the channel.
    /// </summary>
    private sealed class Repeats
    {
        public Repeats(ModbotEvent first, OpenPost? earlier)
        {
            Key = RepeatKey.Of(first);
            Latest = first;
            Count = 1;
            FirstAt = first.OccurredAt;
            LastAt = first.OccurredAt;
            Earlier = earlier;
        }

        /// <summary>What every event in the card has in common.</summary>
        private RepeatKey Key { get; }

        /// <summary>The newest event, which the card is drawn from.</summary>
        public ModbotEvent Latest { get; private set; }

        /// <summary>The events from this pass.</summary>
        public int Count { get; private set; }

        private DateTimeOffset FirstAt { get; set; }

        private DateTimeOffset LastAt { get; set; }

        /// <summary>The post these go into, when they carry it on rather than start one.</summary>
        public OpenPost? Earlier { get; set; }

        /// <summary>The events the card stands for, the earlier post's included.</summary>
        public int Total => Count + (Earlier?.Count ?? 0);

        public DateTimeOffset TotalFirstAt => Earlier is { } e && e.FirstAt < FirstAt ? e.FirstAt : FirstAt;

        public DateTimeOffset TotalLastAt => Earlier is { } e && e.LastAt > LastAt ? e.LastAt : LastAt;

        /// <summary>
        /// Whether the event is a repeat of these: the same change to the same thing by the same
        /// person, touching the same fields, and the card would still cover no more than the window.
        /// </summary>
        public bool Takes(ModbotEvent fact, TimeSpan window)
            => DiscordEventTypes.FoldsRepeats(fact.Type)
               && OpenPost.Within(fact, TotalFirstAt, TotalLastAt, window)
               && RepeatKey.Of(fact) == Key;

        public void Add(ModbotEvent fact)
        {
            Latest = fact;
            Count++;

            if (fact.OccurredAt < FirstAt)
                FirstAt = fact.OccurredAt;

            if (fact.OccurredAt > LastAt)
                LastAt = fact.OccurredAt;
        }
    }

    /// <summary>
    /// What two events share when one is a repeat of the other: the same change, to the same thing
    /// in the same system, by the same person in the same system, touching the same fields.
    /// </summary>
    /// <remarks>
    /// The platform goes with each id because ids are opaque text and two systems' ids may read
    /// alike. The fields go with the change because a card shows only the latest change's, so a run
    /// that mixed different fields would hide the earlier ones.
    /// </remarks>
    private readonly record struct RepeatKey(
        string Type,
        FactPlatform SubjectPlatform,
        string SubjectId,
        FactPlatform? ActorPlatform,
        string? ActorId,
        string Fields)
    {
        public static RepeatKey Of(ModbotEvent fact)
            => new(
                fact.Type,
                fact.SubjectPlatform,
                fact.SubjectId,
                fact.ActorPlatform,
                fact.ActorId,
                EventCard.ChangedFields(fact));
    }

    /// <summary>
    /// The post at the bottom of a channel that repeats can still go into, as the channel's row
    /// remembers it across restarts.
    /// </summary>
    private sealed record OpenPost(
        string MessageId,
        RepeatKey Key,
        int Count,
        DateTimeOffset FirstAt,
        DateTimeOffset LastAt)
    {
        public static OpenPost? Of(DiscordEventChannel place)
            => place is
            {
                RepeatPostId: { Length: > 0 } id,
                RepeatType: { Length: > 0 } type,
                RepeatSubjectPlatform: { } subjectPlatform,
                RepeatSubjectId: { } subject,
                RepeatFields: { } fields,
                RepeatCount: > 0,
                RepeatFirstAt: { } first,
                RepeatLastAt: { } last,
            }
                ? new OpenPost(
                    id,
                    new RepeatKey(type, subjectPlatform, subject, place.RepeatActorPlatform, place.RepeatActorId, fields),
                    place.RepeatCount,
                    first,
                    last)
                : null;

        public bool Takes(ModbotEvent fact, TimeSpan window)
            => DiscordEventTypes.FoldsRepeats(fact.Type)
               && Within(fact, FirstAt, LastAt, window)
               && RepeatKey.Of(fact) == Key;

        /// <summary>Whether the post would still cover no more than the window with this event in it.</summary>
        public static bool Within(ModbotEvent fact, DateTimeOffset firstAt, DateTimeOffset lastAt, TimeSpan window)
        {
            var first = fact.OccurredAt < firstAt ? fact.OccurredAt : firstAt;
            var last = fact.OccurredAt > lastAt ? fact.OccurredAt : lastAt;
            return last - first <= window;
        }

        public static void Remember(DiscordEventChannel place, string messageId, Repeats card)
        {
            place.RepeatPostId = messageId;
            place.RepeatType = card.Latest.Type;
            place.RepeatSubjectPlatform = card.Latest.SubjectPlatform;
            place.RepeatSubjectId = card.Latest.SubjectId;
            place.RepeatActorPlatform = card.Latest.ActorPlatform;
            place.RepeatActorId = card.Latest.ActorId;
            place.RepeatFields = EventCard.ChangedFields(card.Latest);
            place.RepeatCount = card.Total;
            place.RepeatFirstAt = card.TotalFirstAt;
            place.RepeatLastAt = card.TotalLastAt;
        }

        public static void Forget(DiscordEventChannel place)
        {
            place.RepeatPostId = null;
            place.RepeatType = null;
            place.RepeatSubjectPlatform = null;
            place.RepeatSubjectId = null;
            place.RepeatActorPlatform = null;
            place.RepeatActorId = null;
            place.RepeatFields = null;
            place.RepeatCount = 0;
            place.RepeatFirstAt = null;
            place.RepeatLastAt = null;
        }
    }
}
