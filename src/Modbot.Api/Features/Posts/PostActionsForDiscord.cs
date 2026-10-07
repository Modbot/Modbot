using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Posts;

/// <summary>
/// The bot's way into the Marketing tab's posts: checking a post, saving one that is due now, and
/// listing what waits (Discord commands design §3.7 and §4, step 8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The Marketing tab's own code.</strong> A post is checked by <see cref="PostRequests.CheckAsync"/>,
/// the code the composer's preview and Schedule both use, copied onto a row by
/// <see cref="PostRequests.Apply"/>, and recorded as <c>modbot.post.created</c> with the same fields.
/// What the preview shows is therefore what the row holds, and the row is what the Discord sender
/// sends (<see cref="PostTexts.Discord(Post, PostDestination)"/>).
/// </para>
/// <para>
/// <strong>At most once, the post row's way.</strong> Nothing is sent from here. The row is due now;
/// the Discord sender's loop claims it, makes one try with the library's retries off, and looks in
/// the channel when Discord gave no clear answer (posts design §3.5). The row's id is made from the
/// confirmation's key, so the same confirmation pressed twice, even at once, can only ever make one
/// row.
/// </para>
/// <para>
/// <strong>A post that could not go is not saved.</strong> Pause all posting, the Discord posts
/// switch off, or Discord not set up would leave a row waiting, and a person who pressed Post now
/// from Discord is not looking at the Marketing tab to find it there. They are told instead.
/// </para>
/// </remarks>
public sealed class PostActionsForDiscord : IPostActions
{
    public const string NoPermission = "You do not have permission to do that.";
    public const string PausedMessage = "Posting is paused in Modbot.";
    public const string OffMessage = "Discord posts are switched off in Modbot.";
    public const string NotSetUpMessage = "Discord is not set up in Modbot.";
    public const string NotATextChannel = "Pick a text channel.";

    /// <summary>The longest error a list line carries.</summary>
    private const int ErrorLength = 200;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly AccountFacts _facts;
    private readonly IServiceProvider _services;

    public PostActionsForDiscord(ModbotContext db, IModbotClock clock, AccountFacts facts, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(services);

        _db = db;
        _clock = clock;
        _facts = facts;
        _services = services;
    }

    private static bool Manages(StaffMember by) => ModbotAuth.Allows(by.Held, ModbotPermissions.ManagePosts);

    private static bool Sees(StaffMember by) => ModbotAuth.Allows(by.Held, ModbotPermissions.ViewPosts);

    /// <summary>The post as the composer would send it for Discord alone, due now.</summary>
    private static PostRequest Request(PostDraft draft)
        => new(
            draft.Title,
            draft.Text,
            When: PostRequests.WhenNow,
            TimeZone: "UTC",
            Draft: false,
            Discord: new PostDiscordRequest(draft.ChannelId));

    public async Task<PostPreviewAnswer> PreviewAsync(PostDraft draft, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(by);

        if (!Manages(by))
            return new PostPreviewAnswer([NoPermission], null, null);

        var (request, problems) = await CheckAsync(draft, ct);

        return problems.Count > 0
            ? new PostPreviewAnswer(problems, null, request.Discord?.ChannelName)
            : new PostPreviewAnswer([], request.Discord!.Content, request.Discord.ChannelName);
    }

    public async Task<PostNowAnswer> PostNowAsync(string key, PostDraft draft, StaffMember by, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(by);

        if (!Manages(by))
            return new PostNowAnswer(false, false, [NoPermission]);

        var id = IdFor(key);

        // This confirmation saved a post already: it answers with that, and saves nothing more.
        if (await _db.Posts.AsNoTracking().AnyAsync(p => p.Id == id, ct))
            return new PostNowAnswer(false, true, [], id);

        var (request, problems) = await CheckAsync(draft, ct);
        if (problems.Count > 0)
            return new PostNowAnswer(false, false, problems);

        var now = _clock.UtcNow;
        var post = new Post
        {
            Id = id,
            CreatedAt = now,
            CreatedByUserId = by.UserId,
        };

        PostRequests.Apply(post, request, draft: false, now);

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);

            _db.Posts.Add(post);
            await _db.SaveChangesAsync(ct);

            await _facts.RecordAsync(
                FactType.PostCreated, post.Id.ToString(), new Actor(by.UserId, by.Username), PostRequests.Describe(post), ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same confirmation, pressed again at the same moment, made the row first.
            _db.ChangeTracker.Clear();

            if (await _db.Posts.AsNoTracking().AnyAsync(p => p.Id == id, ct))
                return new PostNowAnswer(false, true, [], id);

            throw;
        }

        return new PostNowAnswer(true, false, [], id);
    }

    public async Task<PostListAnswer> ListAsync(int most, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!Sees(by) || most < 1)
            return new PostListAnswer([], 0, [], 0);

        var waiting = PostEndpoints.InList(_db.Posts.AsNoTracking(), PostLists.Scheduled);
        var failed = PostEndpoints.InList(_db.Posts.AsNoTracking(), PostLists.Failed);

        var waitingTotal = await waiting.CountAsync(ct);
        var failedTotal = await failed.CountAsync(ct);

        // The Marketing tab's own order for each list.
        var waitingPosts = await PostEndpoints.Ordered(waiting, PostLists.Scheduled)
            .Include(p => p.Destinations)
            .AsSplitQuery()
            .Take(most)
            .ToListAsync(ct);

        var failedPosts = await PostEndpoints.Ordered(failed, PostLists.Failed)
            .Include(p => p.Destinations)
            .AsSplitQuery()
            .Take(most)
            .ToListAsync(ct);

        var channelIds = waitingPosts.Concat(failedPosts)
            .SelectMany(p => p.Destinations)
            .Where(d => d.Network == PostNetworks.Discord && d.Target.Length > 0)
            .Select(d => d.Target)
            .Distinct()
            .ToList();

        var names = (await _db.DiscordChannels.AsNoTracking()
                .Where(c => channelIds.Contains(c.ChannelId))
                .Select(c => new { c.ChannelId, c.Name })
                .ToListAsync(ct))
            .GroupBy(c => c.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        return new PostListAnswer(
            [.. waitingPosts.Select(p => Line(p, names, failed: false))],
            Math.Max(0, waitingTotal - waitingPosts.Count),
            [.. failedPosts.Select(p => Line(p, names, failed: true))],
            Math.Max(0, failedTotal - failedPosts.Count));
    }

    /// <summary>The check the composer's preview and Schedule make, and what stops the Discord sender sending now.</summary>
    private async Task<(PostRequests.Checked Request, List<string> Problems)> CheckAsync(PostDraft draft, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var (request, problems) = await PostRequests.CheckAsync(_db, Request(draft), now, keptPicture: null, ct);

        // Discord's own list offers voice channels and categories too; a post goes to a text or
        // announcement channel only.
        if (request.Discord?.ChannelType is { } type && type is not (DiscordChannelTypes.Text or DiscordChannelTypes.Announcement))
            problems.Add(NotATextChannel);

        var sites = await PostRequests.SitesAsync(_db, _services.GetService<IDiscordBotStatus>(), ct);

        if (sites.HoldFor(PostNetworks.Discord) is { } hold)
        {
            problems.Add(hold switch
            {
                PostHolds.Paused => PausedMessage,
                PostHolds.Off => OffMessage,
                _ => NotSetUpMessage,
            });
        }

        return (request, problems);
    }

    private static PostLine Line(Post post, IReadOnlyDictionary<string, string> channels, bool failed)
    {
        var where = string.Join(
            ", ",
            post.Destinations
                .OrderBy(d => PostNetworks.All.TakeWhile(n => n != d.Network).Count())
                .Select(d => d.Network switch
                {
                    PostNetworks.Discord => channels.TryGetValue(d.Target, out var name) ? $"Discord #{name}" : "Discord",
                    PostNetworks.VRChat => "VRChat",
                    PostNetworks.Bluesky => "Bluesky",
                    _ => d.Network,
                }));

        var error = failed
            ? post.Destinations.FirstOrDefault(d => d.State == PostDestinationStates.Failed)?.Error
            : null;

        if (error is { Length: > ErrorLength })
            error = error[..ErrorLength];

        return new PostLine(post.Id, PostEndpoints.Headline(post.Title, post.Text), post.SendAt, where, error);
    }

    /// <summary>A post id made from the confirmation's key: the same key can only ever be the same post.</summary>
    public static Guid IdFor(string key)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes("discord-post:" + key))[..16]);
}
