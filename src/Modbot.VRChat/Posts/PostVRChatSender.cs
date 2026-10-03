using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;
using VRChat.API.Model;

namespace Modbot.VRChat.Posts;

/// <param name="Sent">Posts that went out this pass, adopted ones included.</param>
/// <param name="Looked">Looks made in the group's posts for a post that got no clear answer.</param>
/// <param name="Failed">Destinations that turned Failed this pass.</param>
public sealed record PostVRChatPass(int Sent, int Looked, int Failed);

/// <summary>
/// Sends posts to the VRChat group at their time, and looks for the ones VRChat gave no clear
/// answer to (posts design §3.4, §3.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>At most once.</strong> VRChat's post call has no id of Modbot's own to send, and VRChat
/// has answered a calendar create with a 500 and made it anyway (calendar design §3.1), so a
/// destination is claimed (<see cref="PostClaim"/>) before VRChat is asked, with the title and text
/// about to go out. A 5xx, a 408, a timeout, or an answer with no id leaves it Checking: two minutes
/// later the group's posts are read, newest first, and a post by Modbot's own VRChat account whose
/// title and text are the same once both are cut down to letters and digits, made no earlier than
/// two minutes before the attempt, and held by no other destination, is adopted as Posted.
/// <strong>Nothing is ever sent again by itself.</strong> Try again looks once more first.
/// </para>
/// <para>
/// <strong>Not found is an answer only once the whole window was read</strong>, as on Discord: the
/// read has to reach a post older than the window, or the end of the list, and be made after the
/// window ended, since VRChat may still make the post a moment after a 5xx. A read that fails, or a
/// group so busy the pages run out first, is looked at again later and never turns into anything
/// Try again would send without looking; an hour after the looking began it is Failed, "Could not
/// check the group's posts.", still as one VRChat may have.
/// </para>
/// <para>
/// <strong>The audit log adopts too</strong>, at no cost: the audit-log sync already writes
/// VRChat's <c>group.post.create</c> entries as facts, with the author, title and text. One that
/// matches by the same rule adopts a destination still looked for, or one Failed that VRChat may
/// have, up to a day after the attempt, since VRChat can show an entry late.
/// </para>
/// <para>
/// <strong>A rate limit is never sent again by Modbot.</strong> A 429, or a call the gate never
/// sent (a cold stop, a sign-in that is waiting, no account), made nothing: the destination waits
/// again, and the next attempt waits for the gate's own wait to be over (foundation §4.3.1).
/// </para>
/// <para>
/// Calls go through the gate on <c>groups.posts.write</c> for the post and <c>groups.posts</c> for
/// the look, at background priority, so a person on the VRChat page is never queued behind a
/// scheduled post. No id is checked for shape (foundation §3.1.1).
/// </para>
/// </remarks>
public sealed class PostVRChatSender
{
    /// <summary>How many looks one pass makes, at most.</summary>
    public const int LooksPerPass = 2;

    /// <summary>How many posts one page of a look reads.</summary>
    public const int LookSize = 20;

    /// <summary>How many pages one look reads at most before it tries again later.</summary>
    public const int LookPages = 5;

    /// <summary>How long after an unclear answer the group's posts are first read (decision 4).</summary>
    public static readonly TimeSpan FirstLookAfter = TimeSpan.FromMinutes(2);

    /// <summary>How far VRChat's clock may be behind Modbot's for a post to count as made by the attempt.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(2);

    /// <summary>How late an audit-log entry may arrive and still adopt a post.</summary>
    public static readonly TimeSpan AuditLogLateBy = TimeSpan.FromHours(24);

    public const string NotTaken = "VRChat did not add the post.";
    public const string CouldNotCheck = "Could not check the group's posts.";
    public const string TooBusy = "The group posted too much to check yet.";
    public const string NoOwnId = "Modbot's own VRChat id is not known yet.";
    public const string NeedsTitle = "VRChat needs a title.";
    public const string NoText = "The post has no text.";
    public const string GroupChanged = "That group is not Modbot's VRChat group.";

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly PostClaim _claim;
    private readonly ILogger _log;

    public PostVRChatSender(
        IVRChatGate gate,
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        PostClaim claim,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(claim);

        _gate = gate;
        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _claim = claim;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>What a post asks VRChat for (posts design §3.6). Notify only on the first send.</summary>
    public static CreateGroupPostRequest Request(string title, string text, VRChatPostOptions options, bool notify)
    {
        ArgumentNullException.ThrowIfNull(options);

        var roles = PostTexts.VRChatRoles(options);

        return new CreateGroupPostRequest(
            imageId: string.IsNullOrWhiteSpace(options.ImageId) ? null! : options.ImageId,
            roleIds: roles.Count > 0 ? [.. roles] : null!,
            sendNotification: notify,
            text: text,
            title: title,
            visibility: options.Visibility == VRChatPostVisibilities.Everyone ? GroupPostVisibility.Public : GroupPostVisibility.Group);
    }

    /// <summary>
    /// What an edit of a post already in the group asks VRChat for: the whole post again, the picture
    /// id it went with included, because VRChat removes a picture an edit leaves out, and nobody
    /// notified again.
    /// </summary>
    public static CreateGroupPostRequest EditRequest(string title, string text, VRChatPostOptions options) =>
        Request(title, text, options, notify: false);

    public async Task<PostVRChatPass> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var sites = new PostSites(
            settings.PostsPaused,
            settings.DiscordPostsOn,
            DiscordSetUp: false,
            settings.VRChatPostsOn,
            PostSites.VRChatReady(settings));

        var pass = new Pass(settings, _clock.UtcNow, ct);

        await SettleStuckAsync(pass).ConfigureAwait(false);
        await AdoptFromAuditLogAsync(pass).ConfigureAwait(false);
        await LookAsync(pass).ConfigureAwait(false);
        await FailLateAsync(pass).ConfigureAwait(false);

        if (sites.HoldFor(PostNetworks.VRChat) is null)
            await SendOneAsync(pass).ConfigureAwait(false);

        return new PostVRChatPass(pass.Sent, pass.Looked, pass.Failed);
    }

    // ── Cut off mid-send ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destination left Sending -- the process stopped between the claim and the answer -- may
    /// be in the group. It is looked for, never sent again.
    /// </summary>
    private async Task SettleStuckAsync(Pass pass)
    {
        var before = pass.Now - PostRules.StuckSendingAfter;

        var stuck = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.VRChat
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

    // ── The audit log ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adopts a destination that may be in the group from the audit-log sync's own
    /// <c>group.post.create</c> facts: no request to VRChat.
    /// </summary>
    private async Task AdoptFromAuditLogAsync(Pass pass)
    {
        if (pass.AccountId is null)
            return;

        var oldest = pass.Now - AuditLogLateBy;

        var waiting = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.VRChat
                && d.MayBeSent
                && (d.State == PostDestinationStates.Checking || d.State == PostDestinationStates.Failed)
                && d.SentAt != null
                && d.SentAt > oldest
                && d.SentText != null)
            .OrderBy(d => d.SentAt)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        if (waiting.Count == 0)
            return;

        var from = waiting.Min(d => d.SentAt!.Value) - ClockSlack;

        var entries = (await _db.Events.AsNoTracking()
                .Where(e => e.Type == FactType.GroupPostCreated && e.OccurredAt >= from)
                .Select(e => new { e.SubjectId, e.OccurredAt, e.ActorId, e.Data })
                .ToListAsync(pass.Ct).ConfigureAwait(false))
            .Select(e => AuditEntry.Read(e.SubjectId, e.OccurredAt, e.ActorId, e.Data))
            .ToList();

        if (entries.Count == 0)
            return;

        var held = await HeldAsync(pass, except: null).ConfigureAwait(false);

        foreach (var destination in waiting)
        {
            var sentAt = destination.SentAt!.Value;

            var found = entries.FirstOrDefault(e =>
                string.Equals(e.AuthorId, pass.AccountId, StringComparison.Ordinal)
                && (e.GroupId is null || string.Equals(e.GroupId, destination.Target, StringComparison.Ordinal))
                && e.OccurredAt >= sentAt - ClockSlack
                && e.OccurredAt <= sentAt + AuditLogLateBy
                && PostTexts.SameWords(e.Title, destination.SentTitle)
                && PostTexts.SameWords(e.Text, destination.SentText)
                && !held.Contains(e.PostId));

            if (found is null)
                continue;

            var post = await _db.Posts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == destination.PostId, pass.Ct).ConfigureAwait(false);

            if (post is null)
                continue;

            held.Add(found.PostId);
            await PostedAsync(pass, post, destination, found.PostId, found.OccurredAt, adopted: true).ConfigureAwait(false);

            _log.Information(
                "VRChat post {PostId} had gone through after all as {VRChatPostId}, found in the audit log", post.Id, found.PostId);
        }
    }

    /// <summary>One <c>group.post.create</c> fact, as far as the adopt rule needs it.</summary>
    private sealed record AuditEntry(string PostId, DateTimeOffset OccurredAt, string? AuthorId, string? GroupId, string? Title, string? Text)
    {
        public static AuditEntry Read(string postId, DateTimeOffset occurredAt, string? actorId, string data)
        {
            string? author = null, group = null, title = null, text = null;

            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    author = StringOf(root, "authorId");
                    group = StringOf(root, "groupId");
                    title = StringOf(root, "title");
                    text = StringOf(root, "text");
                }
            }
            catch (JsonException)
            {
                // A fact Modbot cannot read adopts nothing.
            }

            return new AuditEntry(postId, occurredAt, author ?? actorId, group, title, text);
        }

        private static string? StringOf(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    // ── The look ─────────────────────────────────────────────────────────────────────────

    private async Task LookAsync(Pass pass)
    {
        var due = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.VRChat
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

            await LookOnceAsync(pass, post, destination).ConfigureAwait(false);
        }
    }

    private async Task LookOnceAsync(Pass pass, Post post, PostDestination destination)
    {
        var attempt = destination.SentAt ?? pass.Now;
        var windowEnds = attempt + FirstLookAfter;

        // Too early to say: VRChat may still make the post. Looked at again just after the window
        // ends, with nothing read and nothing to report.
        if (pass.Now <= windowEnds)
        {
            destination.CheckAt = windowEnds + TimeSpan.FromSeconds(1);
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
            return;
        }

        pass.Looked++;

        var read = pass.AccountId is null || destination.SentText is null
            ? new Window([], NoOwnId, Whole: false, Soon: false)
            : await ReadWindowAsync(pass, destination.Target, attempt).ConfigureAwait(false);

        var held = await HeldAsync(pass, except: destination.Id).ConfigureAwait(false);

        var found = read.Posts.FirstOrDefault(p => IsOurs(p, pass.AccountId, destination, attempt, held));

        if (found is not null)
        {
            var postedAt = found.CreatedAt == default ? pass.Now : AsUtc(found.CreatedAt);
            await PostedAsync(pass, post, destination, found.Id, postedAt, adopted: true).ConfigureAwait(false);

            _log.Information(
                "VRChat post {PostId} had gone through after all as {VRChatPostId}", post.Id, found.Id);
            return;
        }

        // Not found is an answer only once the whole window was read. A read that failed, or a group
        // so busy the pages ran out before the window did, is looked at again, and never turns into
        // anything Try again would send without looking.
        if (read.Error is not null || !read.Whole)
        {
            // An hour from when the looking began: the unclear answer, or a person's Try again.
            if (pass.Now - destination.UpdatedAt >= PostRules.StopLookingAfter)
            {
                await FailAsync(pass, post, destination, CouldNotCheck, mayBeSent: true).ConfigureAwait(false);
                return;
            }

            // Tried again later; the words are kept for Health. UpdatedAt stays when the looking began.
            // A read the gate never sent (a cold stop, a sign-in waiting) is asked again sooner: the
            // gate refuses without sending until its own wait is over.
            var why = read.Error ?? TooBusy;
            destination.CheckAt = pass.Now + (read.Soon ? PostRules.NotSentRetryAfter : PostRules.LookAgainAfter);
            destination.Error = Trim(why);
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

    /// <summary>
    /// Whether a post read from the group is the one an attempt made: by Modbot's own account, the
    /// same title and text once both are cut down to letters and digits, made no earlier than the
    /// attempt less <see cref="ClockSlack"/>, and held by no other destination.
    /// </summary>
    public static bool IsOurs(GroupPost post, string? accountId, PostDestination destination, DateTimeOffset attempt, IReadOnlySet<string> held)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(held);

        return !string.IsNullOrEmpty(post.Id)
            && accountId is not null
            && string.Equals(post.AuthorId, accountId, StringComparison.Ordinal)
            && post.CreatedAt != default
            && AsUtc(post.CreatedAt) >= attempt - ClockSlack
            && PostTexts.SameWords(post.Title, destination.SentTitle)
            && PostTexts.SameWords(post.Text, destination.SentText)
            && !held.Contains(post.Id);
    }

    /// <summary>What a look read, and whether it covered the whole window the post could be in.</summary>
    /// <param name="Soon">The gate never sent the read: asked again sooner than a read that failed.</param>
    private sealed record Window(IReadOnlyList<GroupPost> Posts, string? Error, bool Whole, bool Soon);

    /// <summary>
    /// Reads the group's posts, newest first as VRChat lists them, a page at a time, until it has
    /// seen a post older than the attempt less <see cref="ClockSlack"/> or the end of the list, at
    /// most <see cref="LookPages"/> pages. Whole when either was reached. Made only after the window
    /// ended, so a post VRChat made late is newer than anything read past.
    /// </summary>
    private async Task<Window> ReadWindowAsync(Pass pass, string groupId, DateTimeOffset attempt)
    {
        var windowStarts = attempt - ClockSlack;
        var posts = new List<GroupPost>();

        for (var i = 0; i < LookPages; i++)
        {
            var offset = i * LookSize;

            var result = await _gate.ExecuteAsync(
                new VRChatEndpoint(VRChatEndpointClass.GroupsPosts, groupId, "GetGroupPosts"),
                // `publicOnly` left unset: Modbot's account is in the group and must see the
                // members-only posts it made.
                (client, token) => client.Groups.GetGroupPostsWithHttpInfoAsync(groupId, LookSize, offset, cancellationToken: token),
                VRChatCallPriority.Background,
                pass.Ct).ConfigureAwait(false);

            if (!result.Success)
                return new Window(posts, Reason(result), Whole: false, Soon: NothingMade(result));

            var page = result.Value?.Posts ?? [];
            posts.AddRange(page);

            if (page.Count < LookSize)
                return new Window(posts, null, Whole: true, Soon: false);

            if (page.Any(p => p.CreatedAt != default && AsUtc(p.CreatedAt) < windowStarts))
                return new Window(posts, null, Whole: true, Soon: false);
        }

        return new Window(posts, null, Whole: false, Soon: false);
    }

    /// <summary>The VRChat post ids other destinations hold, so one post is never adopted twice.</summary>
    private async Task<HashSet<string>> HeldAsync(Pass pass, Guid? except)
    {
        var ids = await _db.PostDestinations.AsNoTracking()
            .Where(d => d.Network == PostNetworks.VRChat && d.ExternalId != null && (except == null || d.Id != except))
            .Select(d => d.ExternalId!)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    // ── Too late ─────────────────────────────────────────────────────────────────────────

    private async Task FailLateAsync(Pass pass)
    {
        var waiting = await _db.PostDestinations
            .Join(_db.Posts, d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Destination.Network == PostNetworks.VRChat
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
            .CountAsync(d => d.Network == PostNetworks.VRChat && d.SentAt != null && d.SentAt > hourAgo, pass.Ct)
            .ConfigureAwait(false);

        if (!PostRules.UnderHourlyCap(sentLastHour))
            return;

        // The soonest due first. Tracked: the claim saves these two as they were read.
        var next = await _db.PostDestinations
            .Join(_db.Posts, d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Destination.Network == PostNetworks.VRChat
                && x.Destination.State == PostDestinationStates.Waiting
                && x.Post.Status == PostStatuses.Scheduled
                && x.Post.SendAt != null
                && x.Post.SendAt <= pass.Now
                // One VRChat just turned away waits its turn, so the posts behind it go.
                && (x.Destination.CheckAt == null || x.Destination.CheckAt <= pass.Now))
            .OrderBy(x => x.Post.SendAt)
            .FirstOrDefaultAsync(pass.Ct).ConfigureAwait(false);

        if (next is null || PostRules.IsLate(next.Post, next.Destination, pass.Now))
            return;

        var post = next.Post;
        var destination = next.Destination;
        var title = PostTexts.TitleFor(post, destination);
        var text = PostTexts.TextFor(post, destination);

        if (title is null)
        {
            await FailAsync(pass, post, destination, NeedsTitle, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        if (text.Length == 0)
        {
            await FailAsync(pass, post, destination, NoText, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // Checked again at send time, not only when it was saved: only the group in settings. The
        // group may have changed since.
        var groupId = destination.Target;
        if (pass.GroupId.Length == 0 || !string.Equals(groupId, pass.GroupId, StringComparison.Ordinal))
        {
            await FailAsync(pass, post, destination, GroupChanged, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // What VRChat is sent, kept on the row as it went: the picture only while uploads are on and
        // it is still the post's picture, so an edit later sends exactly that again.
        var chosen = PostTexts.VRChatOptionsOf(destination);
        var imageId = PostTexts.VRChatImageFor(post, chosen, pass.UploadsOn);
        var sent = chosen with
        {
            RoleIds = PostTexts.VRChatRoles(chosen),
            ImageId = imageId,
            PictureId = imageId is null ? null : chosen.PictureId,
        };

        destination.Options = PostTexts.WriteVRChatOptions(sent);

        // When it started waiting, kept for a send that turns out not to have been made.
        var waitingSince = destination.UpdatedAt;

        // Written first. Nothing is sent unless this went through.
        if (!await _claim.ClaimAsync(post, destination, title, text, pass.Ct).ConfigureAwait(false))
            return;

        var request = Request(title, text, sent, notify: chosen.Notify);

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, groupId, "AddGroupPost"),
            (client, token) => client.Groups.AddGroupPostWithHttpInfoAsync(groupId, request, cancellationToken: token),
            VRChatCallPriority.Background,
            pass.Ct).ConfigureAwait(false);

        if (result is { Success: true, Value.Id: { Length: > 0 } postId })
        {
            var created = result.Value!.CreatedAt;
            var postedAt = created == default ? pass.Now : AsUtc(created);
            await PostedAsync(pass, post, destination, postId, postedAt, adopted: false).ConfigureAwait(false);
            return;
        }

        if (!result.Success && NothingMade(result))
        {
            // Nothing was made: a rate limit, or the gate never sent it. It waits, and is not tried
            // again for a while, so the posts behind it are not held up; the gate's own wait decides
            // when anything goes. Its waiting time is left as it was, so a post that never gets
            // through still turns Failed once it is an hour late.
            destination.State = PostDestinationStates.Waiting;
            destination.Error = Trim(Reason(result));
            destination.ErrorAt = pass.Now;
            destination.CheckAt = pass.Now + PostRules.NotSentRetryAfter;
            destination.UpdatedAt = waitingSince;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

            _log.Information("VRChat post {PostId} waits: {Reason}", post.Id, destination.Error);
            return;
        }

        if (result.Success || Unclear(result))
        {
            // Maybe in the group. Looked for in two minutes; never sent again by itself.
            destination.State = PostDestinationStates.Checking;
            destination.MayBeSent = true;
            destination.CheckAt = (destination.SentAt ?? pass.Now) + FirstLookAfter + TimeSpan.FromSeconds(1);
            destination.UpdatedAt = pass.Now;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

            _log.Warning(
                "VRChat gave no clear answer to post {PostId} ({Status}); Modbot will look in the group's posts: {Reason}",
                post.Id, result.StatusCode, result.Success ? "no id" : Reason(result));
            return;
        }

        // A refusal of this post: sending it again would get the same answer.
        var missing = VRChatGroupPermissions.Refusal(
            result.StatusCode, result.Kind, "AddGroupPost", groupId, result.RawResponse, pass.Settings)?.Permission;

        await FailAsync(pass, post, destination, Reason(result), mayBeSent: false, missing).ConfigureAwait(false);
    }

    /// <summary>
    /// Nothing reached VRChat, or VRChat made nothing: a 429, or a call the gate never sent (a cold
    /// stop, a sign-in that is waiting, no account) or that could not find VRChat's address.
    /// </summary>
    internal static bool NothingMade<T>(VRChatResult<T> result) =>
        result.IsRateLimited
        || (result.WasNotSent && result.Kind is VRChatFailureKind.RateLimited
            or VRChatFailureKind.SignInWaiting
            or VRChatFailureKind.NotConfigured
            or VRChatFailureKind.NameResolution);

    /// <summary>An answer that does not say whether the post was made: a 5xx, a 408, a timeout, a lost connection.</summary>
    internal static bool Unclear<T>(VRChatResult<T> result) =>
        !result.Success
        && !NothingMade(result)
        && (result.StatusCode == 0 || result.StatusCode >= 500 || result.StatusCode == 408);

    // ── Outcomes ─────────────────────────────────────────────────────────────────────────

    private async Task PostedAsync(
        Pass pass, Post post, PostDestination destination, string postId, DateTimeOffset postedAt, bool adopted)
    {
        destination.State = PostDestinationStates.Posted;
        destination.ExternalId = postId;
        destination.Link = destination.Target.Length > 0 ? PostTexts.VRChatLink(destination.Target) : null;
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

        await WriteFactAsync(FactType.PostSent, post, pass.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.VRChat,
            ["groupId"] = destination.Target,
            ["link"] = destination.Link,
            ["externalId"] = postId,
            ["sentTitle"] = destination.SentTitle,
            ["sentText"] = destination.SentText,
            ["adopted"] = adopted,
            ["scheduledBy"] = post.CreatedByUserId?.ToString(),
        }, pass.Ct).ConfigureAwait(false);
    }

    private async Task FailAsync(
        Pass pass, Post post, PostDestination destination, string error, bool mayBeSent, string? missingPermission = null)
    {
        destination.State = PostDestinationStates.Failed;
        destination.Error = Trim(error);
        destination.ErrorAt = pass.Now;
        destination.MissingPermission = missingPermission;
        destination.MayBeSent = mayBeSent;
        destination.SendIfMissing = false;
        destination.CheckAt = null;
        destination.UpdatedAt = pass.Now;

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        pass.Failed++;
        _log.Warning("Post {PostId} did not go to VRChat: {Error}", post.Id, destination.Error);

        await WriteFactAsync(FactType.PostFailed, post, pass.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.VRChat,
            ["groupId"] = destination.Target,
            ["error"] = destination.Error,
            ["missingPermission"] = missingPermission,
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

    /// <summary>VRChat's own words when it gave any, otherwise the gate's.</summary>
    private static string Reason<T>(VRChatResult<T> result) =>
        (result.Kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(result.RawResponse))
        ?? result.ErrorMessage
        ?? $"VRChat answered {result.StatusCode}.";

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value,
        }, TimeSpan.Zero);

    private static string? Trim(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

    private sealed class Pass(Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        public Settings Settings { get; } = settings;

        public string GroupId { get; } = settings.ManagedGroupId?.Trim() ?? string.Empty;

        /// <summary>Modbot's own VRChat user id, the author of every post it makes. Null until the group poll read it.</summary>
        public string? AccountId { get; } = string.IsNullOrWhiteSpace(settings.VRChatAccountUserId) ? null : settings.VRChatAccountUserId;

        public bool UploadsOn { get; } = settings.VRChatPictureUploads;

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Ct { get; } = ct;

        public int Sent { get; set; }

        public int Looked { get; set; }

        public int Failed { get; set; }
    }
}
