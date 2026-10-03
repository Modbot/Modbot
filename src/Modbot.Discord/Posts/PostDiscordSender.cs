using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Posts;

/// <param name="Sent">Posts that went out this pass, adopted ones included.</param>
/// <param name="Looked">Looks made in a channel for a post that got no clear answer.</param>
/// <param name="Failed">Destinations that turned Failed this pass.</param>
public sealed record PostDiscordPass(int Sent, int Looked, int Failed);

/// <summary>
/// Sends posts to Discord at their time, and looks for the ones Discord gave no clear answer to
/// (posts design §3.4, §3.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>At most once.</strong> A destination is claimed (<see cref="PostClaim"/>) before Discord
/// is asked, and the send goes with the library's retries off. An answer that is not clear -- no
/// answer, a timeout, a 5xx -- leaves it Checking: a minute later the channel is read after the
/// time of the attempt, and a message by the bot with the same text and the same number of files is
/// adopted as Posted. Nothing found is Failed with "Discord did not take the post."; a look that
/// cannot be made is tried again every fifteen minutes for an hour, then Failed with "Could not
/// check the channel.". <strong>Nothing is ever sent again by itself.</strong> Try again looks
/// once more first.
/// </para>
/// <para>
/// <strong>What holds it.</strong> Pause all posting, the Discord posts switch, and a Discord that is
/// not set up each stop sending; the post waits. One due more than an hour ago is not sent at all
/// and turns Failed with "Not sent on time." (decision 5). Looks carry on while paused: they send
/// nothing.
/// </para>
/// <para>
/// <strong>One send a pass</strong>, every twenty seconds while the bot is connected, and at most
/// <see cref="PostRules.PerSitePerHour"/> an hour, so a mistake cannot flood a channel.
/// </para>
/// <para>
/// Only the database and the gateway: the sender reads what the API saved and asks VRChat nothing.
/// </para>
/// </remarks>
public sealed class PostDiscordSender
{
    /// <summary>How many looks one pass makes, at most.</summary>
    public const int LooksPerPass = 3;

    /// <summary>How many messages one page of a look reads.</summary>
    public const int LookSize = 50;

    /// <summary>How many pages one look reads at most before it tries again later.</summary>
    public const int LookPages = 4;

    public const string TooBusy = "The channel was too busy to check yet.";
    public const string ChannelNotInServer = "That channel is not in the Discord server.";

    public const string NotTaken = "Discord did not take the post.";
    public const string CouldNotCheck = "Could not check the channel.";
    public const string TooLong = "The Discord text is longer than 2000 characters.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly PostClaim _claim;
    private readonly ILogger _log;

    public PostDiscordSender(
        ModbotContext db,
        IModbotClock clock,
        DiscordBotStatus status,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        PostClaim claim,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(claim);

        _db = db;
        _clock = clock;
        _status = status;
        _facts = facts;
        _partitions = partitions;
        _claim = claim;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<PostDiscordPass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var guildId = settings.DiscordGuildId?.Trim();
        var sites = new PostSites(
            settings.PostsPaused,
            settings.DiscordPostsOn,
            DiscordSetUp: !string.IsNullOrWhiteSpace(guildId) && gateway.State == DiscordGatewayState.Ready);

        var pass = new Pass(gateway, guildId ?? string.Empty, _clock.UtcNow, ct);

        await SettleStuckAsync(pass).ConfigureAwait(false);
        await LookAsync(pass).ConfigureAwait(false);
        await FailLateAsync(pass).ConfigureAwait(false);

        if (sites.HoldFor(PostNetworks.Discord) is null)
            await SendOneAsync(pass).ConfigureAwait(false);

        return new PostDiscordPass(pass.Sent, pass.Looked, pass.Failed);
    }

    // ── Cut off mid-send ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destination left Sending -- the process stopped between the claim and the answer -- may
    /// be on Discord. It is looked for, never sent again.
    /// </summary>
    private async Task SettleStuckAsync(Pass pass)
    {
        var before = pass.Now - PostRules.StuckSendingAfter;

        var stuck = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.Discord
                && d.State == PostDestinationStates.Sending
                && (d.SentAt == null || d.SentAt < before))
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        if (stuck.Count == 0)
            return;

        foreach (var destination in stuck)
        {
            destination.State = PostDestinationStates.Checking;
            destination.MayBeSent = true;
            destination.CheckAt = pass.Now;
            destination.SentAt ??= pass.Now;
            destination.UpdatedAt = pass.Now;
        }

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
    }

    // ── The look ─────────────────────────────────────────────────────────────────────────

    private async Task LookAsync(Pass pass)
    {
        var due = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.Discord
                && d.State == PostDestinationStates.Checking
                && (d.CheckAt == null || d.CheckAt <= pass.Now))
            .OrderBy(d => d.CheckAt)
            .Take(LooksPerPass)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        foreach (var destination in due)
        {
            var post = await _db.Posts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == destination.PostId, pass.Ct).ConfigureAwait(false);

            if (post is null)
                continue;

            pass.Looked++;
            await LookOnceAsync(pass, post, destination).ConfigureAwait(false);
        }
    }

    private async Task LookOnceAsync(Pass pass, Post post, PostDestination destination)
    {
        var attempt = destination.SentAt ?? pass.Now;
        var bot = pass.Gateway.BotUserId;

        var read = bot is null || destination.SentText is null
            ? new Window([], "The bot's own id is not known yet.", Whole: false)
            : await ReadWindowAsync(pass, destination.Target, attempt).ConfigureAwait(false);

        var files = post.PictureId is null ? 0 : 1;
        var held = await _db.PostDestinations.AsNoTracking()
            .Where(d => d.Id != destination.Id && d.Network == PostNetworks.Discord && d.Target == destination.Target && d.ExternalId != null)
            .Select(d => d.ExternalId!)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        var found = read.Messages.FirstOrDefault(m =>
            string.Equals(m.AuthorId, bot, StringComparison.Ordinal)
            && string.Equals(m.Text, destination.SentText, StringComparison.Ordinal)
            && m.Attachments.Count == files
            && !held.Contains(m.Id, StringComparer.Ordinal));

        if (found is not null)
        {
            await PostedAsync(pass, post, destination, found.Id, found.SentAt, adopted: true).ConfigureAwait(false);
            return;
        }

        // Not found is an answer only once the whole window was read. A read that failed, or a
        // channel so busy the pages ran out before the window did, is looked at again, and never
        // turns into anything Try again would send.
        if (read.Error is not null || !read.Whole)
        {
            // An hour from when the looking began: the unclear answer, or a person's Try again.
            if (pass.Now - destination.UpdatedAt >= PostRules.StopLookingAfter)
            {
                await FailAsync(pass, post, destination, CouldNotCheck, mayBeSent: true).ConfigureAwait(false);
                return;
            }

            // Too early to say: looked at again just after the window ends, with nothing to report.
            if (read.Error is null && read.Early is { } windowEnds)
            {
                destination.CheckAt = windowEnds + TimeSpan.FromSeconds(1);
                await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
                return;
            }

            // Tried again later; the words are kept for Health. UpdatedAt stays when the looking began.
            var why = read.Error ?? TooBusy;
            destination.CheckAt = pass.Now + PostRules.LookAgainAfter;
            destination.Error = why.Length <= 1024 ? why : why[..1024];
            destination.ErrorAt = pass.Now;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
            return;
        }

        if (destination.SendIfMissing)
        {
            // A person pressed Try again and it is not there: it is sent on the next pass.
            destination.State = PostDestinationStates.Waiting;
            destination.MayBeSent = false;
            destination.SendIfMissing = false;
            destination.CheckAt = null;
            destination.Error = null;
            destination.ErrorAt = null;
            destination.UpdatedAt = pass.Now;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
            return;
        }

        await FailAsync(pass, post, destination, NotTaken, mayBeSent: true).ConfigureAwait(false);
    }

    /// <summary>What a look read, and whether it covered the whole window the post could be in.</summary>
    /// <param name="Early">
    /// Read to the channel's end before the window ended: looked at again just after it ends.
    /// </param>
    private sealed record Window(
        IReadOnlyList<DiscordMessageSnapshot> Messages, string? Error, bool Whole, DateTimeOffset? Early = null);

    /// <summary>
    /// Reads the channel from a minute before the attempt, a page at a time, until it has seen a
    /// message later than <see cref="PostRules.LookWindowAfter"/> after the attempt or the channel's
    /// newest message, at most <see cref="LookPages"/> pages. Whole when either was reached.
    /// </summary>
    private static async Task<Window> ReadWindowAsync(Pass pass, string channelId, DateTimeOffset attempt)
    {
        var after = PostRules.DiscordIdAt(attempt - PostRules.FirstLookAfter);
        var windowEnds = attempt + PostRules.LookWindowAfter;
        var messages = new List<DiscordMessageSnapshot>();

        for (var i = 0; i < LookPages; i++)
        {
            var page = await pass.Gateway.ReadRecentAsync(channelId, after, LookSize, pass.Ct).ConfigureAwait(false);

            if (page.Error is not null)
                return new Window(messages, page.Error, Whole: false);

            messages.AddRange(page.Messages);

            // A short page is the channel's newest message: everything since the attempt was read,
            // but only once the window has ended. Before that, Discord may still make the message a
            // moment later (it can after a 5xx), so the channel's end is not yet an answer.
            if (!page.Full || page.NewestId is null)
                return pass.Now > windowEnds
                    ? new Window(messages, null, Whole: true)
                    : new Window(messages, null, Whole: false, Early: windowEnds);

            if (PostRules.DiscordTimeOf(page.NewestId) is { } newest && newest > windowEnds)
                return new Window(messages, null, Whole: true);

            after = page.NewestId;
        }

        return new Window(messages, null, Whole: false);
    }

    // ── Too late ─────────────────────────────────────────────────────────────────────────

    private async Task FailLateAsync(Pass pass)
    {
        var waiting = await _db.PostDestinations
            .Join(_db.Posts, d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Destination.Network == PostNetworks.Discord
                && x.Destination.State == PostDestinationStates.Waiting
                && x.Post.Status == PostStatuses.Scheduled
                && x.Post.SendAt != null
                && x.Post.SendAt <= pass.Now)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        foreach (var row in waiting.Where(x => PostRules.IsLate(x.Post, x.Destination, pass.Now)))
            await FailAsync(pass, row.Post, row.Destination, PostRules.NotSentOnTime, mayBeSent: false).ConfigureAwait(false);
    }

    // ── The send ─────────────────────────────────────────────────────────────────────────

    private async Task SendOneAsync(Pass pass)
    {
        var hourAgo = pass.Now - TimeSpan.FromHours(1);
        var sentLastHour = await _db.PostDestinations
            .CountAsync(d => d.Network == PostNetworks.Discord && d.SentAt != null && d.SentAt > hourAgo, pass.Ct)
            .ConfigureAwait(false);

        if (!PostRules.UnderHourlyCap(sentLastHour))
            return;

        // The soonest due first. Tracked: the claim saves these two as they were read.
        var next = await _db.PostDestinations
            .Join(_db.Posts, d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Destination.Network == PostNetworks.Discord
                && x.Destination.State == PostDestinationStates.Waiting
                && x.Post.Status == PostStatuses.Scheduled
                && x.Post.SendAt != null
                && x.Post.SendAt <= pass.Now
                // One Discord just turned away waits its turn, so the posts behind it go.
                && (x.Destination.CheckAt == null || x.Destination.CheckAt <= pass.Now))
            .OrderBy(x => x.Post.SendAt)
            .FirstOrDefaultAsync(pass.Ct).ConfigureAwait(false);

        if (next is null || PostRules.IsLate(next.Post, next.Destination, pass.Now))
            return;

        var post = next.Post;
        var destination = next.Destination;
        var title = PostTexts.TitleFor(post, destination);
        var text = PostTexts.Discord(post, destination);
        var options = PostTexts.DiscordOptionsOf(destination);

        if (text.Length == 0)
        {
            await FailAsync(pass, post, destination, "The post has no text.", mayBeSent: false).ConfigureAwait(false);
            return;
        }

        if (!PostTexts.DiscordFits(text))
        {
            await FailAsync(pass, post, destination, TooLong, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // Checked again at send time, not only when it was saved: only a channel of the server in
        // settings, as the bot last listed it. The server or the channel may have changed since.
        var guildId = pass.GuildId;
        var target = destination.Target;
        var inServer = guildId.Length > 0
            && await _db.DiscordChannels.AsNoTracking()
                .AnyAsync(c => c.ChannelId == target && c.GuildId == guildId && c.RemovedAt == null, pass.Ct)
                .ConfigureAwait(false);

        if (!inServer)
        {
            await FailAsync(pass, post, destination, ChannelNotInServer, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        var pictures = await PicturesAsync(post, pass.Ct).ConfigureAwait(false);

        if (post.PictureId is not null && pictures.Count == 0)
        {
            await FailAsync(pass, post, destination, "The post's picture is gone. Choose it again.", mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // When it started waiting, kept for a send that turns out not to have been made.
        var waitingSince = destination.UpdatedAt;

        // Written first. Nothing is sent unless this went through.
        if (!await _claim.ClaimAsync(post, destination, title, text, pass.Ct).ConfigureAwait(false))
            return;

        var outcome = await pass.Gateway.SendPostAsync(destination.Target, text, options.RoleId, pictures, pass.Ct).ConfigureAwait(false);

        if (outcome is { Sent: true, MessageId: { } messageId })
        {
            await PostedAsync(pass, post, destination, messageId, pass.Now, adopted: false).ConfigureAwait(false);
            return;
        }

        if (outcome.Sent || outcome.Unclear)
        {
            // Maybe in the channel. Looked for in a minute; never sent again by itself.
            destination.State = PostDestinationStates.Checking;
            destination.MayBeSent = true;
            destination.CheckAt = (destination.SentAt ?? pass.Now) + PostRules.FirstLookAfter;
            destination.UpdatedAt = pass.Now;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

            _log.Warning(
                "Discord gave no clear answer to post {PostId}; Modbot will look in the channel: {Error}",
                post.Id, outcome.Error);
            return;
        }

        if (!outcome.Permanent)
        {
            // Nothing was made: a rate limit, or the channel could not be looked up. It waits, and
            // is not tried again for a while, so the posts behind it are not held up. Its waiting
            // time is left as it was, so a post that never gets through still turns Failed once it
            // is an hour late.
            destination.State = PostDestinationStates.Waiting;
            destination.Error = Trim(outcome.Error);
            destination.ErrorAt = pass.Now;
            destination.CheckAt = pass.Now + PostRules.NotSentRetryAfter;
            destination.UpdatedAt = waitingSince;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
            return;
        }

        await FailAsync(pass, post, destination, outcome.Error ?? "Discord refused the post.", mayBeSent: false).ConfigureAwait(false);
    }

    /// <summary>The post's picture as the file Discord is sent, or none.</summary>
    private async Task<IReadOnlyList<DiscordPicture>> PicturesAsync(Post post, CancellationToken ct)
    {
        if (post.PictureId is not { } id)
            return [];

        var picture = await _db.CalendarCoverPictures.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Bytes, c.ContentType })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (picture is null || CardPictures.Extension(picture.ContentType) is not { } extension)
            return [];

        return [new DiscordPicture(PictureName(id) + extension, picture.Bytes)];
    }

    /// <summary>The name a post's picture goes by on Discord, before its extension.</summary>
    public static string PictureName(Guid id) => "picture-" + id.ToString("N");

    // ── Outcomes ─────────────────────────────────────────────────────────────────────────

    private async Task PostedAsync(
        Pass pass, Post post, PostDestination destination, string messageId, DateTimeOffset postedAt, bool adopted)
    {
        destination.State = PostDestinationStates.Posted;
        destination.ExternalId = messageId;
        destination.Link = pass.GuildId.Length > 0 ? PostTexts.DiscordLink(pass.GuildId, destination.Target, messageId) : null;
        destination.PostedAt = postedAt;
        destination.MayBeSent = false;
        destination.SendIfMissing = false;
        destination.CheckAt = null;
        destination.Error = null;
        destination.ErrorAt = null;
        destination.MissingPermission = null;
        destination.UpdatedAt = pass.Now;

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        pass.Sent++;
        _status.Posted(1, pass.Now);

        await WriteFactAsync(FactType.PostSent, post, pass.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.Discord,
            ["channelId"] = destination.Target,
            ["link"] = destination.Link,
            ["externalId"] = messageId,
            ["sentText"] = destination.SentText,
            ["adopted"] = adopted,
            ["scheduledBy"] = post.CreatedByUserId?.ToString(),
        }, pass.Ct).ConfigureAwait(false);

        if (PostTexts.DiscordOptionsOf(destination).Publish && destination.PublishedAt is null)
            await PublishAsync(pass, destination, messageId).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes to the channel's followers after the post is in. A refusal leaves the post Posted
    /// and says "Not published"; Try again does that step alone.
    /// </summary>
    private async Task PublishAsync(Pass pass, PostDestination destination, string messageId)
    {
        var outcome = await pass.Gateway.PublishAsync(destination.Target, messageId, pass.Ct).ConfigureAwait(false);

        if (outcome.Sent)
        {
            destination.PublishedAt = pass.Now;
            destination.Error = null;
            destination.ErrorAt = null;
        }
        else
        {
            destination.Error = Trim(outcome.Error ?? "Discord did not publish the post.");
            destination.ErrorAt = pass.Now;
        }

        destination.UpdatedAt = pass.Now;
        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
    }

    private async Task FailAsync(Pass pass, Post post, PostDestination destination, string error, bool mayBeSent)
    {
        destination.State = PostDestinationStates.Failed;
        destination.Error = Trim(error);
        destination.ErrorAt = pass.Now;
        destination.MayBeSent = mayBeSent;
        destination.SendIfMissing = false;
        destination.CheckAt = null;
        destination.UpdatedAt = pass.Now;

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        pass.Failed++;
        _log.Warning("Post {PostId} did not go to Discord: {Error}", post.Id, destination.Error);

        await WriteFactAsync(FactType.PostFailed, post, pass.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.Discord,
            ["channelId"] = destination.Target,
            ["error"] = destination.Error,
        }, pass.Ct).ConfigureAwait(false);
    }

    private async Task WriteFactAsync(string type, Post post, DateTimeOffset now, JsonObject data, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = post.Id.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            ct).ConfigureAwait(false);
    }

    private static string? Trim(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

    private sealed class Pass(IDiscordGateway gateway, string guildId, DateTimeOffset now, CancellationToken ct)
    {
        public IDiscordGateway Gateway { get; } = gateway;

        public string GuildId { get; } = guildId;

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Ct { get; } = ct;

        public int Sent { get; set; }

        public int Looked { get; set; }

        public int Failed { get; set; }
    }
}
