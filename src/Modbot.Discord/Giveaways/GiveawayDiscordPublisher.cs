using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Giveaways;

/// <param name="Calls">How many Discord calls the pass made.</param>
/// <param name="Error">The first refusal, when there was one.</param>
public sealed record GiveawayDiscordPass(int Calls, string? Error = null);

/// <summary>
/// Keeps each giveaway's Discord post in line with the giveaway, and names the winners in the
/// channel when it is drawn (giveaways design §7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The post is written only when what it should say changes</strong>, by comparing a
/// fingerprint with the one last sent — the same path the calendar's publisher takes, and for the
/// same reason: an edit, the giveaway closing, the entry count moving and the draw all arrive by
/// one route, and a quiet pass costs no Discord calls at all.
/// </para>
/// <para>
/// <strong>The winners are announced once per draw.</strong> A re-draw is a new draw with a new
/// number, so it gets its own announcement; the number on the post is what stops the same draw
/// being announced twice, which matters because the pass runs every twenty seconds.
/// </para>
/// <para>
/// Nobody is pinged, ever. A giveaway with four hundred entrants would otherwise be four hundred
/// notifications for one result.
/// </para>
/// </remarks>
public sealed class GiveawayDiscordPublisher
{
    /// <summary>How many Discord calls one pass may make.</summary>
    public const int CallsPerPass = 5;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ILogger _log;

    public GiveawayDiscordPublisher(
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

    public async Task<GiveawayDiscordPass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var publicAddress = settings.PublicAddress?.TrimEnd('/');
        var now = _clock.UtcNow;

        var posts = await _db.GiveawayPosts
            .Where(p => p.State != GiveawayPostStates.Removed)
            .ToListAsync(ct).ConfigureAwait(false);

        var withPosts = posts.Select(p => p.GiveawayId).Distinct().ToList();

        var giveaways = await _db.Giveaways
            .Where(g => withPosts.Contains(g.Id)
                || (g.PostToChannel && g.DeletedAt == null && g.State != GiveawayStates.Draft))
            .OrderBy(g => g.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (giveaways.Count == 0)
            return new GiveawayDiscordPass(0);

        var ids = giveaways.Select(g => g.Id).ToList();

        var entryCounts = await _db.GiveawayEntries.AsNoTracking()
            .Where(e => ids.Contains(e.GiveawayId) && e.WithdrawnAt == null)
            .GroupBy(e => e.GiveawayId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, ct).ConfigureAwait(false);

        var latestDraws = await LatestDrawsAsync(ids, ct).ConfigureAwait(false);
        var drawIds = latestDraws.Values.Select(d => d.Id).ToList();

        var winners = await _db.GiveawayEntrants.AsNoTracking()
            .Where(e => drawIds.Contains(e.DrawId) && e.WinnerRank != null)
            .OrderBy(e => e.WinnerRank)
            .ToListAsync(ct).ConfigureAwait(false);

        var roleNames = await RoleNamesAsync(settings.DiscordGuildId, ct).ConfigureAwait(false);
        var pass = new Pass(gateway, publicAddress, now, ct);

        foreach (var giveaway in giveaways)
        {
            if (pass.Calls >= CallsPerPass)
                break;

            var post = posts.FirstOrDefault(p => p.GiveawayId == giveaway.Id);
            latestDraws.TryGetValue(giveaway.Id, out var draw);

            var theirs = draw is null
                ? []
                : winners.Where(w => w.DrawId == draw.Id).ToList();

            post = await SyncPostAsync(
                pass, giveaway, post, entryCounts.GetValueOrDefault(giveaway.Id), theirs, roleNames).ConfigureAwait(false);

            if (post is not null && draw is not null && pass.Calls < CallsPerPass)
                await AnnounceAsync(pass, giveaway, post, draw, theirs).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var (giveaway, error) in pass.Failures)
        {
            await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
            await _facts.WriteAsync(
                new FactRecord
                {
                    Type = FactType.GiveawayPublishFailed,
                    OccurredAt = now,
                    SubjectPlatform = FactPlatform.Modbot,
                    SubjectId = giveaway.Id.ToString(),
                    Source = FactSource.Modbot,
                    Data = new JsonObject { ["name"] = giveaway.Name, ["error"] = error },
                },
                ct).ConfigureAwait(false);
        }

        if (pass.Written > 0)
            _status.Posted(pass.Written, now);

        return new GiveawayDiscordPass(pass.Calls, pass.Failures.FirstOrDefault().Error);
    }

    private async Task<GiveawayPost?> SyncPostAsync(
        Pass pass,
        Giveaway giveaway,
        GiveawayPost? post,
        int entryCount,
        IReadOnlyList<GiveawayEntrant> winners,
        IReadOnlyDictionary<string, string> roleNames)
    {
        var channelId = giveaway.ChannelId?.Trim();
        var gone = giveaway.DeletedAt is not null;

        // Unticked, moved to another channel, or deleted: take the old message down. A cancelled
        // giveaway keeps its post and says "Cancelled", because people who entered deserve to see
        // what became of it.
        if (post?.MessageId is { } existing && post.ChannelId is { } postedIn
            && (gone || !giveaway.PostToChannel || channelId != postedIn || giveaway.State == GiveawayStates.Draft))
        {
            var deleted = await pass.Call(g => g.DeleteMessageAsync(postedIn, existing, "Giveaway post turned off", pass.Ct))
                .ConfigureAwait(false);

            if (!deleted.Sent && !deleted.Permanent)
            {
                Fail(pass, giveaway, post, deleted, fingerprint: null);
                return post;
            }

            Forget(post, GiveawayPostStates.Removed, pass.Now);
            pass.Written++;
        }

        var wants = giveaway.PostToChannel
            && channelId is { Length: > 0 }
            && !gone
            && giveaway.State != GiveawayStates.Draft;

        if (!wants)
        {
            if (post is { MessageId: null } && post.State != GiveawayPostStates.Removed)
            {
                post.State = GiveawayPostStates.Removed;
                post.UpdatedAt = pass.Now;
            }

            return post;
        }

        if (pass.Calls >= CallsPerPass)
            return post;

        post ??= AddPost(giveaway);

        var state = StateOf(giveaway);
        var link = Link(pass.PublicAddress, giveaway);
        var embed = GiveawayCard.For(giveaway, state, entryCount, winners, roleNames, link);
        var links = GiveawayCard.Links(link);

        var fingerprint = CalendarFingerprint.Of(
            "giveawayPost", channelId, embed.Title, embed.Color, embed.Url, embed.Footer,
            string.Join('\n', embed.Fields.Select(f => f.Name + "=" + f.Value)),
            links.Count > 0 ? links[0].Url : null);

        if (post.MessageId is not null && post.SentFingerprint == fingerprint)
            return post;

        if (post.State == GiveawayPostStates.Failed && post.FailedFingerprint == fingerprint)
            return post;

        DiscordPostOutcome outcome;

        if (post.MessageId is null)
        {
            outcome = await pass.Call(g => g.PostAsync(channelId!, null, [embed], links, pass.Ct)).ConfigureAwait(false);

            if (outcome is { Sent: true, MessageId: { } posted })
            {
                post.MessageId = posted;
                post.ChannelId = channelId;

                // Something to click. Without it everybody has to find the emoji themselves, and
                // half of them will pick a different one and wonder why they are not entered.
                if (giveaway.EntryWay == GiveawayEntryWays.React && pass.Calls < CallsPerPass)
                {
                    var reacted = await pass.Call(g => g.AddReactionAsync(channelId!, posted, giveaway.Emoji, pass.Ct))
                        .ConfigureAwait(false);

                    if (!reacted.Sent)
                        _log.Warning("Could not put the giveaway's own reaction on {Giveaway}: {Reason}", giveaway.Id, reacted.Error);
                }
            }
        }
        else
        {
            var id = post.MessageId;
            var inChannel = post.ChannelId ?? channelId!;
            outcome = await pass.Call(g => g.EditAsync(inChannel, id, null, [embed], links, pass.Ct)).ConfigureAwait(false);

            // Somebody deleted the post. Not posted again until the giveaway changes, so a
            // moderator who deleted it on purpose is not argued with every twenty seconds.
            if (!outcome.Sent && outcome.Permanent)
            {
                post.MessageId = null;
                post.SentFingerprint = null;
            }
        }

        if (!outcome.Sent)
        {
            Fail(pass, giveaway, post, outcome, fingerprint);
            return post;
        }

        Published(post, fingerprint, pass.Now);
        pass.Written++;
        return post;
    }

    /// <summary>Names the winners in the channel, once per draw.</summary>
    private async Task AnnounceAsync(
        Pass pass, Giveaway giveaway, GiveawayPost post, GiveawayDraw draw, IReadOnlyList<GiveawayEntrant> winners)
    {
        if (post.AnnouncedDraws >= draw.Number || post.ChannelId is not { } channelId)
            return;

        var text = GiveawayCard.Announcement(giveaway, draw, winners);
        var link = Link(pass.PublicAddress, giveaway);

        var outcome = await pass
            .Call(g => g.PostAsync(channelId, text, [], GiveawayCard.Links(link), pass.Ct))
            .ConfigureAwait(false);

        if (!outcome.Sent)
        {
            // Not a post failure on the giveaway itself: the card is fine, the line about it is
            // not. Tried again next pass, since the draw number has not moved.
            _log.Warning("Could not announce the giveaway {Giveaway}: {Reason}", giveaway.Id, outcome.Error);
            return;
        }

        post.AnnouncedDraws = draw.Number;
        post.UpdatedAt = pass.Now;
        pass.Written++;

        await _partitions.EnsureForAsync(pass.Now, pass.Ct).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.GiveawayWinnerAnnounced,
                OccurredAt = pass.Now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = giveaway.Id.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["name"] = giveaway.Name,
                    ["drawNumber"] = draw.Number,
                    ["channelId"] = channelId,
                    ["winners"] = winners.Count,
                },
            },
            pass.Ct).ConfigureAwait(false);
    }

    /// <summary>The newest draw of each giveaway, which is the one the post talks about.</summary>
    private async Task<Dictionary<Guid, GiveawayDraw>> LatestDrawsAsync(
        IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var draws = await _db.GiveawayDraws.AsNoTracking()
            .Where(d => ids.Contains(d.GiveawayId))
            .ToListAsync(ct).ConfigureAwait(false);

        return draws
            .GroupBy(d => d.GiveawayId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Number).First());
    }

    private async Task<IReadOnlyDictionary<string, string>> RoleNamesAsync(string? guildId, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (guildId is { Length: > 0 })
        {
            var roles = await _db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == guildId)
                .Select(r => new { r.RoleId, r.Name })
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var role in roles)
                names[role.RoleId] = role.Name;
        }

        return names;
    }

    /// <summary>The giveaway's page on this Modbot, when the public address is known.</summary>
    public static string? Link(string? publicAddress, Giveaway giveaway)
    {
        ArgumentNullException.ThrowIfNull(giveaway);

        return string.IsNullOrWhiteSpace(publicAddress)
            ? null
            : $"{publicAddress.TrimEnd('/')}/giveaways?giveaway={giveaway.Id:D}";
    }

    public static GiveawayCardState StateOf(Giveaway giveaway)
    {
        ArgumentNullException.ThrowIfNull(giveaway);

        return giveaway.State switch
        {
            GiveawayStates.Cancelled => GiveawayCardState.Cancelled,
            GiveawayStates.Drawn => GiveawayCardState.Drawn,
            GiveawayStates.Closed => GiveawayCardState.Closed,
            _ => GiveawayCardState.Open,
        };
    }

    private GiveawayPost AddPost(Giveaway giveaway)
    {
        var row = new GiveawayPost
        {
            GiveawayId = giveaway.Id,
            State = GiveawayPostStates.Waiting,
            UpdatedAt = _clock.UtcNow,
        };

        _db.GiveawayPosts.Add(row);
        return row;
    }

    private static void Published(GiveawayPost post, string fingerprint, DateTimeOffset now)
    {
        post.State = GiveawayPostStates.Published;
        post.SentFingerprint = fingerprint;
        post.FailedFingerprint = null;
        post.Error = null;
        post.ErrorAt = null;
        post.UpdatedAt = now;
    }

    private static void Forget(GiveawayPost post, string state, DateTimeOffset now)
    {
        post.MessageId = null;
        post.SentFingerprint = null;
        post.FailedFingerprint = null;
        post.Error = null;
        post.ErrorAt = null;
        post.State = state;
        post.UpdatedAt = now;
    }

    private void Fail(Pass pass, Giveaway giveaway, GiveawayPost post, DiscordPostOutcome outcome, string? fingerprint)
    {
        var error = outcome.Error ?? "Discord refused.";
        var repeated = post.State == GiveawayPostStates.Failed && post.Error == error;

        post.State = GiveawayPostStates.Failed;
        post.Error = error.Length <= 1024 ? error : error[..1023] + "…";
        post.ErrorAt = pass.Now;
        post.FailedFingerprint = outcome.Permanent ? fingerprint : null;
        post.UpdatedAt = pass.Now;

        // One fact per new problem, not one every twenty seconds while it lasts.
        if (!repeated)
            pass.Failures.Add((giveaway, post.Error));

        _log.Warning("Could not update the post for the giveaway {Giveaway}: {Reason}", giveaway.Id, error);
    }

    private sealed class Pass(IDiscordGateway gateway, string? publicAddress, DateTimeOffset now, CancellationToken ct)
    {
        public string? PublicAddress { get; } = publicAddress;

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Ct { get; } = ct;

        public int Calls { get; private set; }

        public int Written { get; set; }

        public List<(Giveaway Giveaway, string Error)> Failures { get; } = [];

        public Task<DiscordPostOutcome> Call(Func<IDiscordGateway, Task<DiscordPostOutcome>> call)
        {
            Calls++;
            return call(gateway);
        }
    }
}
