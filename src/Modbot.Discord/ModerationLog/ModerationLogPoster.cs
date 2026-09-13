using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.ModerationLog;

public enum ModerationLogPassOutcome
{
    /// <summary>No channel is set. Nothing read.</summary>
    NoChannel = 1,

    /// <summary>The channel was just turned on: the cursor moved to the newest fact and nothing was posted.</summary>
    StartedFromNow = 2,

    /// <summary>Nothing new since the cursor, or nothing new of a type the channel carries.</summary>
    NothingNew = 3,

    /// <summary>At least one message went out.</summary>
    Posted = 4,

    /// <summary>The first message was refused. The cursor did not move.</summary>
    Failed = 5,
}

/// <param name="Read">Facts read past the cursor this pass, of every type.</param>
/// <param name="Posted">Events that reached the channel.</param>
/// <param name="Error">Why posting stopped, when it did. Set on <see cref="ModerationLogPassOutcome.Failed"/> and on a pass that posted some but not all.</param>
public sealed record ModerationLogPass(ModerationLogPassOutcome Outcome, int Read, int Posted, string? Error);

/// <summary>
/// Reads new facts from the log and posts the moderation events among them to the configured
/// channel, one pass at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The cursor is a fact id on the settings row</strong>, the same shape the profile sync
/// uses to discover people: "every row after this one" is an index range on the log's primary
/// key. Facts are read in id order regardless of type and the cursor moves past all of them, so
/// a busy night of instance joins does not leave the poster re-reading the same page.
/// </para>
/// <para>
/// <strong>Turning the channel on posts nothing.</strong> A cursor of zero means the poster has not
/// started; it jumps to the newest fact and says so. The alternative -- replaying months of
/// bans into a channel the moment somebody pastes an id -- is what every operator would expect
/// to be protected from, and the setting's hint says as much.
/// </para>
/// <para>
/// <strong>Only listed types leave the building.</strong> The chosen set is read through
/// <see cref="ModerationLogEvents.Parse"/>, which cuts it down to group audit-log types. Account
/// facts, reset links and sign-ins are not in that list and cannot be added to it from the
/// outside, so they never reach Discord whatever the column says.
/// </para>
/// <para>
/// <strong>Pacing.</strong> A backlog goes out ten events to a message, a second and a bit apart,
/// at most a handful of messages per pass. A refused post stops the pass with the cursor at the
/// last event that did go out, so nothing is skipped and nothing is repeated.
/// </para>
/// </remarks>
public sealed class ModerationLogPoster
{
    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly ModerationLogOptions _options;
    private readonly ILogger _log;

    public ModerationLogPoster(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        DiscordBotStatus status,
        ModerationLogOptions? options = null,
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
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<ModerationLogPass> RunOnceAsync(
        IDiscordGateway gateway,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(delay);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var channelId = settings.DiscordLogChannelId?.Trim();

        if (string.IsNullOrEmpty(channelId))
        {
            // Cleared. Forget where we were, so turning it back on later starts from then.
            if (settings.DiscordLogPostedThrough is not null)
            {
                settings.DiscordLogPostedThrough = null;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return new ModerationLogPass(ModerationLogPassOutcome.NoChannel, 0, 0, null);
        }

        if (settings.DiscordLogPostedThrough is not { } cursor)
        {
            // An empty log has no newest fact; zero -- everything after fact 0 -- is then right.
            var newest = await _db.Events.AsNoTracking().MaxAsync(e => (long?)e.Id, ct).ConfigureAwait(false) ?? 0;
            settings.DiscordLogPostedThrough = newest;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _log.Information(
                "Discord moderation log channel turned on; posting events from now on (after fact #{Id}) and none of the history before it",
                newest);

            return new ModerationLogPass(ModerationLogPassOutcome.StartedFromNow, 0, 0, null);
        }

        var wanted = ModerationLogEvents.Parse(settings.DiscordLogEventTypes);

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.Id > cursor)
            .OrderBy(e => e.Id)
            .Take(_options.FactsPerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return new ModerationLogPass(ModerationLogPassOutcome.NothingNew, 0, 0, null);

        var matching = rows.Where(r => wanted.Contains(r.Type)).ToList();

        if (matching.Count == 0)
        {
            settings.DiscordLogPostedThrough = rows[^1].Id;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new ModerationLogPass(ModerationLogPassOutcome.NothingNew, rows.Count, 0, null);
        }

        var names = await DisplayNames.LoadAsync(
                _db, matching.Select(m => m.SubjectId).Concat(matching.Select(m => m.ActorId)), ct)
            .ConfigureAwait(false);

        var embeds = matching
            .Select(m => (m.Id, Embed: ModerationEventEmbed.For(ModerationEventView.From(m, names), settings.PublicAddress)))
            .ToList();

        var posted = 0;
        var postedThrough = cursor;
        var messages = 0;
        string? error = null;

        foreach (var chunk in embeds.Chunk(_options.EmbedsPerMessage))
        {
            if (messages >= _options.MessagesPerPass)
                break;

            if (messages > 0)
                await delay(_options.GapBetweenMessages, ct).ConfigureAwait(false);

            var outcome = await gateway.PostAsync(channelId, chunk.Select(c => c.Embed).ToList(), ct).ConfigureAwait(false);
            messages++;

            if (!outcome.Sent)
            {
                error = outcome.Error ?? "Discord refused the message.";
                _status.Problem(error, _clock.UtcNow);
                _log.Warning("Could not post to the Discord moderation log channel: {Reason}", error);
                break;
            }

            posted += chunk.Length;
            postedThrough = chunk[^1].Id;
        }

        if (posted == 0)
            return new ModerationLogPass(ModerationLogPassOutcome.Failed, rows.Count, 0, error);

        // Everything that matched went out, so the trailing facts of other types are read too.
        if (postedThrough == matching[^1].Id)
            postedThrough = rows[^1].Id;

        var now = _clock.UtcNow;
        settings.DiscordLogPostedThrough = postedThrough;

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
                    ["fromId"] = embeds[0].Id,
                    ["toId"] = postedThrough,
                },
            }, ct)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _status.Posted(posted, now);

        return new ModerationLogPass(ModerationLogPassOutcome.Posted, rows.Count, posted, error);
    }
}
