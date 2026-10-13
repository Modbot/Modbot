using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.LivePosts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Notifications;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.LivePosts;

/// <summary>
/// The "we're live" group post pass: nothing while the setting is off, a draft (and a message to the
/// host) or a post sent at once as the setting says, once per instance, only for the instances the
/// scope names, and never for an instance that was running before the setting was turned on.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LivePostPassTests
{
    private const string Group = "grp_livepost_test";
    private const string World = "wrld_livepost_test";

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 19, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(Start);

    public LivePostPassTests(PostgresFixture db) => _db = db;

    private sealed class RecordingNotifier : INotifier
    {
        public List<Notification> Raised { get; } = [];

        public Task<NotificationOutcome> RaiseAsync(Notification notification, CancellationToken ct = default)
        {
            Raised.Add(notification);
            return Task.FromResult(NotificationOutcome.Nothing);
        }
    }

    // --- The plain parts ---

    [Fact]
    public void TheWordsNameWhatStartedAndLinkToIt()
    {
        var (title, text) = LivePostPass.Words("Movie night", "https://vrchat.com/home/launch?worldId=w&instanceId=1");

        Assert.Equal("Movie night", title);
        Assert.Equal("Movie night has started.\nhttps://vrchat.com/home/launch?worldId=w&instanceId=1", text);
        Assert.Equal("Movie night has started.", LivePostPass.Words("Movie night", null).Text);
    }

    [Fact]
    public void TheMessageLinksToTheDraftOnlyWhenModbotKnowsItsOwnAddress()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Assert.Equal(
            "A group post for Movie night is waiting for you to approve. https://modbot.example/marketing/drafts?post=11111111-1111-1111-1111-111111111111",
            LivePostPass.DraftMessage("Movie night", "https://modbot.example/", id));
        Assert.Equal(
            "A group post for Movie night is waiting for you to approve.",
            LivePostPass.DraftMessage("Movie night", null, id));
    }

    [Theory]
    [InlineData(LivePostScopes.Off, true, true, false)]
    [InlineData(LivePostScopes.Any, false, false, true)]
    [InlineData(LivePostScopes.Calendar, false, false, false)]
    [InlineData(LivePostScopes.Calendar, false, true, true)]
    [InlineData(LivePostScopes.Calendar, true, false, true)]
    [InlineData(LivePostScopes.Modbot, false, true, false)]
    [InlineData(LivePostScopes.Modbot, true, false, true)]
    public void TheScopeNamesTheInstances(string scope, bool openedByModbot, bool ofCalendarEvent, bool expected)
        => Assert.Equal(expected, LivePostPass.InScope(scope, openedByModbot, ofCalendarEvent));

    // --- The pass ---

    private async Task SetUpAsync(string scope, bool auto = false, DateTimeOffset? from = null)
    {
        await using var context = _db.NewContext();
        await context.Posts.Where(p => p.Kind == PostKinds.InstanceLive).ExecuteDeleteAsync(Ct);
        await context.CalendarOpenings.Where(o => context.CalendarEvents.Any(e => e.Id == o.EventId && e.WorldId == World)).ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.Where(e => e.WorldId == World).ExecuteDeleteAsync(Ct);
        await context.VRChatInstances.Where(i => i.GroupId == Group).ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        settings.PublicAddress = "https://modbot.example";
        settings.LivePostScope = scope;
        settings.LivePostAuto = auto;
        settings.LivePostFrom = from ?? Start.AddDays(-1);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<VRChatInstance> AddInstanceAsync(int people = 3, DateTimeOffset? openedAt = null, string? name = null)
    {
        await using var context = _db.NewContext();

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = $"{World}:{Random.Shared.Next(10000, 99999)}~group({Group})~groupAccessType(members)",
            WorldId = World,
            GroupId = Group,
            Name = name,
            OpenedAt = openedAt ?? Start.AddMinutes(-5),
            LastSeenAt = Start,
            LastUserCount = people,
            PeakUserCount = people,
            SeenInGroupList = true,
        };

        context.VRChatInstances.Add(instance);
        await context.SaveChangesAsync(Ct);
        return instance;
    }

    private async Task<CalendarEvent> AddEventAsync(Guid? host = null, bool announceItself = false, TimeSpan? startsIn = null)
    {
        await using var context = _db.NewContext();

        var starts = Start + (startsIn ?? TimeSpan.Zero);
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            StartsAt = starts,
            EndsAt = starts.AddHours(2),
            OccurrenceStartsAt = starts,
            TimeZone = "UTC",
            State = CalendarEventStates.Scheduled,
            WorldId = World,
            InviteHostUserId = host,
            AnnounceFirstJoinInVRChat = announceItself,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        context.CalendarEvents.Add(calendarEvent);
        await context.SaveChangesAsync(Ct);
        return calendarEvent;
    }

    private async Task OpenAsync(CalendarEvent calendarEvent, VRChatInstance instance, Guid? openedBy = null)
    {
        await using var context = _db.NewContext();

        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = calendarEvent.Id,
            OccurrenceStartsAt = calendarEvent.StartsAt,
            AttemptedAt = Start.AddMinutes(-10),
            Location = instance.Location,
            InstanceId = instance.Id,
            OpenedByUserId = openedBy,
        });

        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> PassAsync(RecordingNotifier notifier)
    {
        await using var context = _db.NewContext();
        var pass = new LivePostPass(
            context,
            _clock,
            notifier,
            new AccountFacts(new FactWriter(context, _clock), new EventPartitionMaintainer(context, _clock), _clock));

        return await pass.RunOnceAsync(Ct);
    }

    private async Task<List<Post>> PostsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.Kind == PostKinds.InstanceLive)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task NothingIsMadeWhileItIsOff()
    {
        await SetUpAsync(LivePostScopes.Off);
        await AddInstanceAsync();
        var notifier = new RecordingNotifier();

        Assert.Equal(0, await PassAsync(notifier));

        Assert.Empty(await PostsAsync());
        Assert.Empty(notifier.Raised);
    }

    [Fact]
    public async Task AnyInstanceGetsADraftForVRChat_AndEveryoneWhoManagesPostsHearsOfIt()
    {
        await SetUpAsync(LivePostScopes.Any);
        var instance = await AddInstanceAsync(name: "Friday hangout");
        var notifier = new RecordingNotifier();

        Assert.Equal(1, await PassAsync(notifier));

        var post = Assert.Single(await PostsAsync());
        Assert.Equal(PostStatuses.Draft, post.Status);
        Assert.Equal(instance.Id.ToString(), post.ExternalKey);
        Assert.Equal("Friday hangout", post.Title);
        Assert.StartsWith("Friday hangout has started.", post.Text);
        Assert.Null(post.SendAt);

        var destination = Assert.Single(post.Destinations);
        Assert.Equal(PostNetworks.VRChat, destination.Network);
        Assert.Equal(Group, destination.Target);

        var told = Assert.Single(notifier.Raised);
        Assert.Equal(NotificationKinds.LivePostDraft, told.Kind);
        Assert.Equal(ModbotPermissions.ManagePosts, told.Audience.Permission);
        Assert.Equal($"/marketing/drafts?post={post.Id}", told.Link);
        Assert.Contains($"https://modbot.example/marketing/drafts?post={post.Id}", told.Body);
    }

    [Fact]
    public async Task AutoPostsAtOnce_WithNoDraftAndNoMessage()
    {
        await SetUpAsync(LivePostScopes.Any, auto: true);
        await AddInstanceAsync();
        var notifier = new RecordingNotifier();

        Assert.Equal(1, await PassAsync(notifier));

        var post = Assert.Single(await PostsAsync());
        Assert.Equal(PostStatuses.Scheduled, post.Status);
        Assert.Equal(Start, post.SendAt);
        Assert.Empty(notifier.Raised);
    }

    [Fact]
    public async Task OneInstanceGetsOnePost_HoweverOftenThePassRuns()
    {
        await SetUpAsync(LivePostScopes.Any);
        await AddInstanceAsync();
        var notifier = new RecordingNotifier();

        Assert.Equal(1, await PassAsync(notifier));
        Assert.Equal(0, await PassAsync(notifier));

        Assert.Single(await PostsAsync());
        Assert.Single(notifier.Raised);
    }

    [Fact]
    public async Task AnInstanceWithNobodyInIt_OneThatOpenedBeforeTheSettingWasTurnedOn_OrOneThatIsOld_GetsNothing()
    {
        await SetUpAsync(LivePostScopes.Any, from: Start.AddMinutes(-30));
        await AddInstanceAsync(people: 0);
        await AddInstanceAsync(openedAt: Start.AddMinutes(-45));
        var notifier = new RecordingNotifier();

        Assert.Equal(0, await PassAsync(notifier));

        _clock.UtcNow = Start.AddHours(3);
        await AddInstanceAsync(openedAt: Start.AddHours(3).AddMinutes(-150));
        Assert.Equal(0, await PassAsync(notifier));
        _clock.UtcNow = Start;

        Assert.Empty(await PostsAsync());
    }

    [Fact]
    public async Task AnInstanceModbotOpened_IsTheEventsWithItsTitle_AndItsHostHearsOfTheDraft()
    {
        await SetUpAsync(LivePostScopes.Modbot);
        await using (var context = _db.NewContext())
        {
            var host = await TestAccounts.CreateAsync(
                context, $"host_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.ManagePosts, ct: Ct);

            var instance = await AddInstanceAsync();
            var calendarEvent = await AddEventAsync(host: host.Id);
            await OpenAsync(calendarEvent, instance);
            var notifier = new RecordingNotifier();

            Assert.Equal(1, await PassAsync(notifier));

            var post = Assert.Single(await PostsAsync());
            Assert.Equal("Movie night", post.Title);

            var told = Assert.Single(notifier.Raised);
            Assert.Equal([host.Id], told.Audience.UserIds);
        }
    }

    [Fact]
    public async Task AHostWhoCannotManagePosts_IsNotTold_ButEveryoneWhoCanIs()
    {
        await SetUpAsync(LivePostScopes.Calendar);
        await using var context = _db.NewContext();
        var host = await TestAccounts.CreateAsync(
            context, $"viewer_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.ViewPosts, ct: Ct);

        var instance = await AddInstanceAsync();
        var calendarEvent = await AddEventAsync(host: host.Id);
        await OpenAsync(calendarEvent, instance);
        var notifier = new RecordingNotifier();

        Assert.Equal(1, await PassAsync(notifier));

        var told = Assert.Single(notifier.Raised);
        Assert.Null(told.Audience.UserIds);
        Assert.Equal(ModbotPermissions.ManagePosts, told.Audience.Permission);
    }

    [Fact]
    public async Task WithNoHost_TheStaffMemberWhoPressedOpenNowIsTold()
    {
        await SetUpAsync(LivePostScopes.Modbot);
        await using var context = _db.NewContext();
        var staff = await TestAccounts.CreateAsync(
            context, $"staff_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.ManagePosts, ct: Ct);

        var instance = await AddInstanceAsync();
        await OpenAsync(await AddEventAsync(), instance, openedBy: staff.Id);
        var notifier = new RecordingNotifier();

        Assert.Equal(1, await PassAsync(notifier));

        Assert.Equal([staff.Id], Assert.Single(notifier.Raised).Audience.UserIds);
    }

    [Fact]
    public async Task AnInstanceOfAnEventsWorldWhileItIsOn_IsTheCalendarsButNotOneModbotOpened()
    {
        await SetUpAsync(LivePostScopes.Calendar);
        await AddEventAsync();
        await AddInstanceAsync();
        var notifier = new RecordingNotifier();

        Assert.Equal(1, await PassAsync(notifier));
        Assert.Equal("Movie night", Assert.Single(await PostsAsync()).Title);

        await SetUpAsync(LivePostScopes.Modbot);
        await AddEventAsync();
        await AddInstanceAsync();

        Assert.Equal(0, await PassAsync(new RecordingNotifier()));
        Assert.Empty(await PostsAsync());
    }

    [Fact]
    public async Task AnEventThatAnnouncesItself_IsLeftToItsOwnPost()
    {
        await SetUpAsync(LivePostScopes.Any);
        var instance = await AddInstanceAsync();
        await OpenAsync(await AddEventAsync(announceItself: true), instance);
        var notifier = new RecordingNotifier();

        Assert.Equal(0, await PassAsync(notifier));

        Assert.Empty(await PostsAsync());
        Assert.Empty(notifier.Raised);
    }
}
