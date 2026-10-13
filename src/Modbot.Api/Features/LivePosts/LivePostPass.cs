using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Twitch;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Notifications;
using Modbot.Core.Time;
using Modbot.Core.Twitch;
using Npgsql;
using Serilog;

namespace Modbot.Api.Features.LivePosts;

/// <summary>
/// Makes the "we're live" post for a group instance once somebody is in it, as the setting asks:
/// a draft for a host to approve, or a post sent straight away.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which instances.</strong> <see cref="LivePostScopes"/>: only the ones Modbot opened itself
/// (a calendar opening holds the instance), the ones of a calendar event (those, plus an instance in
/// the world of an event that is on), or every instance of the managed group. "Somebody is in" is the
/// group instance poll's own count on the instance's row; nothing here asks VRChat.
/// </para>
/// <para>
/// <strong>Once per instance.</strong> The post carries the instance's id as its key under a unique
/// index, so a restart, a second pass or a second copy of Modbot cannot make two. Instances first seen
/// before the setting was turned on, or more than <see cref="Newest"/> ago, get nothing.
/// </para>
/// <para>
/// <strong>An event that posts for itself is left alone.</strong> An event with "Announce in VRChat when
/// the first person joins" ticked already posts through <c>CalendarFirstJoinVRChatPost</c>, as it did
/// before this setting; making another post for its instance would say it twice.
/// </para>
/// <para>
/// <strong>The host.</strong> The event's host, else the staff account that pressed Open now, is told
/// about a draft. A host who cannot manage posts, or an instance with neither, goes to everyone who
/// can. A post sent straight away makes no draft and tells nobody. Times come from
/// <see cref="IModbotClock"/>.
/// </para>
/// </remarks>
public sealed class LivePostPass
{
    /// <summary>How old an instance may be, counted from when Modbot first saw it, and still get a post.</summary>
    public static readonly TimeSpan Newest = TimeSpan.FromHours(2);

    /// <summary>How long before an event starts its instance may open and still count as that event's.</summary>
    public static readonly TimeSpan EarlyOpening = TimeSpan.FromHours(1);

    /// <summary>The most instances one pass makes a post for.</summary>
    private const int PerPass = 10;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly INotifier _notifier;
    private readonly AccountFacts _facts;
    private readonly ILogger _log;

    public LivePostPass(
        ModbotContext db, IModbotClock clock, INotifier notifier, AccountFacts facts, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(facts);

        _db = db;
        _clock = clock;
        _notifier = notifier;
        _facts = facts;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The post's title and words: what has started, and the link to join it.</summary>
    public static (string Title, string Text) Words(string name, string? joinLink)
    {
        var title = name.Length <= Post.MaxTitleLength ? name : name[..Post.MaxTitleLength];
        var text = joinLink is null ? $"{name} has started." : $"{name} has started.\n{joinLink}";
        return (title, text);
    }

    /// <summary>The message to the host: what is waiting and, when Modbot knows its own address, where.</summary>
    public static string DraftMessage(string name, string? publicAddress, Guid postId)
    {
        var text = $"A group post for {name} is waiting for you to approve.";
        return string.IsNullOrWhiteSpace(publicAddress)
            ? text
            : $"{text} {publicAddress.Trim().TrimEnd('/')}{DraftPath(postId)}";
    }

    /// <summary>The path in Modbot that opens a draft.</summary>
    public static string DraftPath(Guid postId) => $"/marketing/drafts?post={postId}";

    /// <summary>Whether an instance is in the scope the setting names.</summary>
    public static bool InScope(string scope, bool openedByModbot, bool ofCalendarEvent) => scope switch
    {
        LivePostScopes.Any => true,
        LivePostScopes.Calendar => openedByModbot || ofCalendarEvent,
        LivePostScopes.Modbot => openedByModbot,
        _ => false,
    };

    /// <returns>How many posts were made.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!LivePostScopes.IsKnown(settings.LivePostScope)
            || settings.LivePostScope == LivePostScopes.Off
            || settings.LivePostFrom is not { } turnedOnAt
            || settings.ManagedGroupId is not { Length: > 0 } groupId)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var since = turnedOnAt > now - Newest ? turnedOnAt : now - Newest;

        var instances = await _db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId
                     && i.ClosedAt == null
                     && i.OpenedAt >= since
                     && ((i.LastUserCount ?? 0) > 0 || (i.PeakUserCount ?? 0) > 0))
            .OrderBy(i => i.OpenedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (instances.Count == 0)
            return 0;

        var keys = instances.Select(i => i.Id.ToString()).ToList();
        var done = (await _db.Posts.AsNoTracking()
                .Where(p => p.Kind == PostKinds.InstanceLive && p.ExternalKey != null && keys.Contains(p.ExternalKey))
                .Select(p => p.ExternalKey!)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var made = 0;

        foreach (var instance in instances)
        {
            if (made >= PerPass)
                break;

            if (done.Contains(instance.Id.ToString()))
                continue;

            if (await MakeAsync(settings, instance, now, ct).ConfigureAwait(false))
                made++;
        }

        return made;
    }

    private async Task<bool> MakeAsync(
        Core.Data.Entities.Settings settings, VRChatInstance instance, DateTimeOffset now, CancellationToken ct)
    {
        var opening = await _db.CalendarOpenings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.InstanceId == instance.Id, ct).ConfigureAwait(false);

        CalendarEvent? calendarEvent = null;

        if (opening is not null)
        {
            calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            // The event posts for itself, as it did before this setting.
            if (calendarEvent is { AnnounceFirstJoinInVRChat: true })
                return false;
        }
        else if (settings.LivePostScope is LivePostScopes.Calendar or LivePostScopes.Any)
        {
            calendarEvent = await EventOfAsync(instance, ct).ConfigureAwait(false);
        }

        if (!InScope(settings.LivePostScope, opening is not null, calendarEvent is not null))
            return false;

        var name = calendarEvent?.Title is { Length: > 0 } title ? title : await NameOfAsync(instance, ct).ConfigureAwait(false);
        var (postTitle, postText) = Words(name, InstanceJoinLink.For(instance.Location));

        var places = new TwitchPostPlaces(VRChat: new TwitchVRChatPlace());
        var draft = !settings.LivePostAuto;
        var request = TwitchPosts.RequestFor(places, postTitle, postText, draft);

        // Problems are not refusals here: the instance is open now. A post the VRChat group cannot
        // take fails on its sender's words, and a draft shows what to fix when it is opened.
        var (checkedRequest, _) = await PostRequests.CheckAsync(_db, request, now, keptPicture: null, ct).ConfigureAwait(false);

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            CreatedByUserId = null,
            Kind = PostKinds.InstanceLive,
            ExternalKey = instance.Id.ToString(),
        };

        PostRequests.Apply(post, checkedRequest, draft, now);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        _db.Posts.Add(post);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_post_kind_external_key",
        })
        {
            // Another pass made it first: the unique key on the instance is the once.
            _db.ChangeTracker.Clear();
            return false;
        }

        await _facts.RecordAsync(FactType.PostCreated, post.Id.ToString(), actor: null, PostRequests.Describe(post), ct)
            .ConfigureAwait(false);

        if (draft)
            await TellHostAsync(settings, calendarEvent, opening, name, post.Id, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _log.Information(
            "Made the {Kind} for the instance {InstanceId}", draft ? "live post draft" : "live post", instance.Id);

        return true;
    }

    /// <summary>
    /// The event whose world this instance is in, while the event is on or about to be: a live event, in
    /// its current date's window, that Modbot did not open. Null for none.
    /// </summary>
    private async Task<CalendarEvent?> EventOfAsync(VRChatInstance instance, CancellationToken ct)
    {
        var candidates = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.WorldId == instance.WorldId && e.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        return candidates.FirstOrDefault(e =>
        {
            if (!CalendarEventStates.IsLive(e.State))
                return false;

            var occurrence = CalendarRepeat.Current(e);
            return instance.OpenedAt >= occurrence.StartsAt - EarlyOpening && instance.OpenedAt < occurrence.EndsAt;
        });
    }

    /// <summary>What to call an instance that is not an event: its own name, else its world's.</summary>
    private async Task<string> NameOfAsync(VRChatInstance instance, CancellationToken ct)
    {
        if (instance.Name is { Length: > 0 } own)
            return own;

        var world = await _db.VRChatWorlds.AsNoTracking()
            .Where(w => w.WorldId == instance.WorldId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(world) ? "A group instance" : world.Trim();
    }

    private async Task TellHostAsync(
        Core.Data.Entities.Settings settings,
        CalendarEvent? calendarEvent,
        CalendarOpening? opening,
        string name,
        Guid postId,
        CancellationToken ct)
    {
        var audience = NotificationAudience.Holding(ModbotPermissions.ManagePosts);

        if ((calendarEvent?.InviteHostUserId ?? opening?.OpenedByUserId) is { } hostId)
        {
            var host = await _db.Users.AsNoTracking()
                .Include(u => u.Roles).ThenInclude(r => r.Role)
                .FirstOrDefaultAsync(u => u.Id == hostId && !u.IsDisabled, ct).ConfigureAwait(false);

            if (host is not null
                && (host.EffectivePermissions.HasFlag(ModbotPermissions.Administrator)
                    || host.EffectivePermissions.HasFlag(ModbotPermissions.ManagePosts)))
            {
                audience = NotificationAudience.These([host.Id]);
            }
        }

        await _notifier.RaiseAsync(
            new Notification(
                NotificationKinds.LivePostDraft,
                NotificationSeverity.Warning,
                "Modbot: a live post is ready",
                DraftMessage(name, settings.PublicAddress, postId),
                audience)
            {
                SameAs = $"{NotificationKinds.LivePostDraft}:{postId}",
                Link = DraftPath(postId),
            },
            ct).ConfigureAwait(false);
    }
}
