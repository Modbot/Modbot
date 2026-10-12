using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Posts;

/// <summary>
/// Keep updated (posts design §8): edits a post that has gone out when an event in one of its
/// lists is published, changed or cancelled.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Looks, not listens.</strong> The calendar is written from a dozen places (the form, the
/// Discord and VRChat sides, the feed, the import), none of which says "an event changed". Each pass
/// builds the lists of every post with Keep updated again (<see cref="PostListUpdates"/>) and edits
/// only when one reads differently, so no kind of change can be missed.
/// </para>
/// <para>
/// <strong>At most one edit a window.</strong> A post is looked at once per <see cref="Window"/>
/// after a change was found, so a run of calendar edits makes one edit of the post, with whatever
/// the lists say by then. The window lives in <see cref="PostListWindow"/>, shared by every pass.
/// </para>
/// <para>
/// <strong>Never again on a refusal.</strong> A site that does not take an edit (a rate limit
/// included) gets no second try in the same pass, and none until the window is over. The edits go
/// through <see cref="IDiscordPostActions"/> and <see cref="IVRChatPostActions"/>, which never ping
/// or notify again, and VRChat's goes through the posts lane. Bluesky cannot edit, so its post keeps
/// the first version. Nothing is edited while Pause all posting is on.
/// </para>
/// <para>
/// The post's own text is written with the new lists only once every site that needed the edit has
/// taken it, so a site that was turned away is tried again with the old list still to find.
/// </para>
/// </remarks>
public sealed class PostListUpdater
{
    /// <summary>How long a post is left alone after a change was found in its lists.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const int WriteTries = 3;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IDiscordPostActions _discord;
    private readonly IVRChatPostActions _vrchat;
    private readonly PostListWindow _window;
    private readonly ILogger _log;

    public PostListUpdater(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IDiscordPostActions discord,
        IVRChatPostActions vrchat,
        PostListWindow window,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(vrchat);
        ArgumentNullException.ThrowIfNull(window);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _discord = discord;
        _vrchat = vrchat;
        _window = window;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>One pass over the posts with Keep updated. How many sites were edited.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null || settings.PostsPaused)
            return 0;

        var ids = await _db.Posts.AsNoTracking()
            .Where(p => p.KeepUpdated
                && p.Status == PostStatuses.Scheduled
                && p.Destinations.Any(d => d.State == PostDestinationStates.Posted
                    && (d.Network == PostNetworks.Discord || d.Network == PostNetworks.VRChat)))
            .Select(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var edited = 0;
        var stopped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var now = _clock.UtcNow;

            if (!_window.IsOver(id, now))
                continue;

            _db.ChangeTracker.Clear();
            var post = await _db.Posts.AsNoTracking().Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
            if (post is null)
                continue;

            var changes = await PostListUpdates.ChangesAsync(_db, post.Text, now, ct).ConfigureAwait(false);
            if (changes.Count == 0)
                continue;

            _window.Started(id, now);
            edited += await UpdateAsync(post, changes, stopped, now, ct).ConfigureAwait(false);
        }

        return edited;
    }

    private async Task<int> UpdateAsync(
        Post post, IReadOnlyList<PostListChange> changes, HashSet<string> stopped, DateTimeOffset now, CancellationToken ct)
    {
        var done = new List<Guid>();
        var gone = new List<Guid>();
        var wasText = new Dictionary<Guid, string?>();
        var nowText = new Dictionary<Guid, string>();
        var turnedAway = false;

        foreach (var destination in post.Destinations.Where(d => d.State == PostDestinationStates.Posted
            && d.Network is PostNetworks.Discord or PostNetworks.VRChat
            && d.ExternalId is { Length: > 0 }
            && d.SentText is { Length: > 0 }))
        {
            var sent = PostListUpdates.ForSite(destination.Network, destination.SentText!, changes);
            if (string.Equals(sent, destination.SentText, StringComparison.Ordinal))
                continue;

            if (stopped.Contains(destination.Network))
            {
                turnedAway = true;
                continue;
            }

            var outcome = await EditAsync(post, destination, sent, ct).ConfigureAwait(false);

            if (outcome is null)
            {
                // Nothing can be sent: a site's words the edit cannot be made with. Left as it is.
                continue;
            }

            if (outcome.Done)
            {
                done.Add(destination.Id);
                wasText[destination.Id] = destination.SentText;
                nowText[destination.Id] = sent;
            }
            else if (outcome.Gone)
            {
                gone.Add(destination.Id);
            }
            else
            {
                // Not taken (a limit, the bot off, a refusal): nothing more is asked of this site
                // until the window is over.
                stopped.Add(destination.Network);
                turnedAway = true;
                _log.Information(
                    "A post's event list was not updated on {Network}: {Reason}", destination.Network, outcome.Error ?? "not connected");
            }
        }

        if (done.Count > 0 || gone.Count > 0 || !turnedAway)
            await WriteAsync(post, changes, done, gone, !turnedAway, now, ct).ConfigureAwait(false);

        foreach (var destinationId in done)
        {
            var destination = post.Destinations.First(d => d.Id == destinationId);

            await WriteFactAsync(
                FactType.PostEdited,
                post,
                now,
                new JsonObject
                {
                    ["title"] = post.Title,
                    ["network"] = destination.Network,
                    ["before"] = wasText[destinationId],
                    ["after"] = nowText[destinationId],
                    ["automatic"] = true,
                },
                ct).ConfigureAwait(false);
        }

        foreach (var destinationId in gone)
        {
            var destination = post.Destinations.First(d => d.Id == destinationId);

            await WriteFactAsync(
                FactType.PostRemoved,
                post,
                now,
                new JsonObject { ["title"] = post.Title, ["network"] = destination.Network, ["by"] = null, ["link"] = destination.Link },
                ct).ConfigureAwait(false);
        }

        return done.Count;
    }

    // Null: the edit cannot be made (VRChat needs a title; Discord's message would be too long).
    private async Task<PostSiteOutcome?> EditAsync(Post post, PostDestination destination, string sent, CancellationToken ct)
    {
        if (destination.Network == PostNetworks.Discord)
        {
            if (!PostTexts.DiscordFits(sent))
                return null;

            return await _discord.EditAsync(destination.Target, destination.ExternalId!, sent, ct).ConfigureAwait(false);
        }

        var title = PostTexts.TidyTitle(destination.SentTitle) ?? PostTexts.TitleFor(post, destination);
        if (title is null)
            return null;

        var options = PostTexts.VRChatOptionsOf(destination);

        return await _vrchat.EditAsync(
            destination.Target, destination.ExternalId!, title, sent, options.Visibility, PostTexts.VRChatRoles(options), options.ImageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes what the sites now have onto the rows, and, when every site is settled, the new lists
    /// into the post's text. The sites have been edited, so a write that loses to another change is
    /// read again and made again, as the Marketing tab does after an edit.
    /// </summary>
    private async Task WriteAsync(
        Post read, IReadOnlyList<PostListChange> changes, List<Guid> done, List<Guid> gone, bool settled, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < WriteTries; attempt++)
        {
            _db.ChangeTracker.Clear();

            var post = await _db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == read.Id, ct).ConfigureAwait(false);
            if (post is null)
                return;

            foreach (var destination in post.Destinations.Where(d => d.State == PostDestinationStates.Posted))
            {
                if (gone.Contains(destination.Id))
                {
                    destination.State = PostDestinationStates.Removed;
                    destination.UpdatedAt = now;
                }
                else if (done.Contains(destination.Id))
                {
                    destination.SentText = PostListUpdates.ForSite(destination.Network, destination.SentText ?? string.Empty, changes);

                    if (destination.TextOverride is { } own)
                        destination.TextOverride = PostListUpdates.ForSite(destination.Network, own, changes);

                    destination.UpdatedAt = now;
                }
            }

            if (settled)
                post.Text = PostListUpdates.Swap(post.Text, changes);

            if (done.Count > 0)
                post.ListsUpdatedAt = now;

            post.Version++;

            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Written since it was read: read it again and make the same change again.
            }
        }

        _db.ChangeTracker.Clear();
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
}

/// <summary>
/// When each post last had a change found in its lists, so a run of calendar edits makes one edit of
/// the post (<see cref="PostListUpdater.Window"/>). Kept in memory and shared by every pass; after a
/// restart a post is looked at again at once, which edits it only if its lists read differently.
/// </summary>
public sealed class PostListWindow
{
    private readonly Dictionary<Guid, DateTimeOffset> _started = [];
    private readonly Lock _gate = new();

    /// <summary>The post has not had a change found within <see cref="PostListUpdater.Window"/> of <paramref name="now"/>.</summary>
    public bool IsOver(Guid postId, DateTimeOffset now)
    {
        lock (_gate)
            return !_started.TryGetValue(postId, out var at) || now - at >= PostListUpdater.Window;
    }

    public void Started(Guid postId, DateTimeOffset now)
    {
        lock (_gate)
            _started[postId] = now;
    }
}
