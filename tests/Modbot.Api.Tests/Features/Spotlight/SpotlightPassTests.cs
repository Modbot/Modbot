using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Spotlight;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Twitch;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Spotlight;

/// <summary>
/// The weekly spotlight pass (spotlight design §4 and §6): nothing is made while it is off, one
/// Marketing draft a week however often the pass runs, a member who opted in in random mode and none
/// yet in staff mode, no draft in a week nobody opted in, and no repeat of someone featured lately.
/// Nothing is ever sent by the pass.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SpotlightPassTests
{
    private const string Guild = "424242";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A Monday, an hour after the default time (Monday 09:00 UTC).
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ThisWeek = new(2026, 10, 12);

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(Start);

    public SpotlightPassTests(PostgresFixture db) => _db = db;

    /// <summary>
    /// The spotlight on, in random mode, with nothing ticked, no weeks, no drafts, and these members
    /// in the server: Ada, Bo and Cy opted in; Dee did not; Music (a bot) and Eli (left) did.
    /// </summary>
    private async Task SetUpAsync(Action<Core.Data.Entities.Settings>? change = null, bool optedIn = true)
    {
        await using var context = _db.NewContext();
        await context.SpotlightWeeks.ExecuteDeleteAsync(Ct);
        await context.Posts.Where(p => p.Kind == PostKinds.Spotlight).ExecuteDeleteAsync(Ct);
        await context.MemberProfiles.ExecuteDeleteAsync(Ct);
        await context.DiscordMembers.Where(m => m.GuildId == Guild).ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = false;
        settings.SpotlightOn = true;
        settings.SpotlightDay = 1;
        settings.SpotlightHour = 9;
        settings.SpotlightTimeZone = null;
        settings.SpotlightMode = SpotlightModes.Random;
        settings.SpotlightPlaces = "{}";
        settings.SpotlightHandledThrough = null;
        change?.Invoke(settings);

        context.DiscordMembers.AddRange(
            Member("1", "Ada"),
            Member("2", "Bo"),
            Member("3", "Cy"),
            Member("4", "Dee"),
            Member("5", "Music", isBot: true),
            Member("6", "Eli", left: true));

        foreach (var id in optedIn ? new[] { "1", "2", "3", "5", "6" } : [])
            context.MemberProfiles.Add(new MemberProfile { DiscordUserId = id, Spotlight = true, UpdatedAt = Start });

        await context.SaveChangesAsync(Ct);
    }

    private static DiscordMember Member(string id, string name, bool isBot = false, bool left = false) => new()
    {
        GuildId = Guild,
        UserId = id,
        Username = name.ToLowerInvariant(),
        DisplayName = name,
        IsBot = isBot,
        FirstSeenAt = Start.AddDays(-30),
        UpdatedAt = Start,
        LeftAt = left ? Start.AddDays(-1) : null,
    };

    private async Task<bool> PassAsync(Func<int, int>? random = null)
    {
        await using var context = _db.NewContext();
        var pass = new SpotlightPass(
            context,
            _clock,
            new AccountFacts(new FactWriter(context, _clock), new EventPartitionMaintainer(context, _clock), _clock),
            random: random ?? (_ => 0));

        return await pass.RunOnceAsync(Ct);
    }

    private async Task<List<SpotlightWeek>> WeeksAsync()
    {
        await using var context = _db.NewContext();
        return await context.SpotlightWeeks.AsNoTracking().OrderBy(w => w.Week).ToListAsync(Ct);
    }

    private async Task<List<Post>> DraftsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.Kind == PostKinds.Spotlight)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task NothingIsMadeWhileItIsOff()
    {
        await SetUpAsync(s => s.SpotlightOn = false);

        Assert.False(await PassAsync());

        Assert.Empty(await WeeksAsync());
        Assert.Empty(await DraftsAsync());
    }

    [Fact]
    public async Task RandomModeMakesADraftOfAMemberWhoOptedIn_AndSendsNothing()
    {
        await SetUpAsync();

        Assert.True(await PassAsync(_ => 0));

        var week = Assert.Single(await WeeksAsync());
        Assert.Equal(ThisWeek, week.Week);
        Assert.Equal("1", week.DiscordUserId);
        Assert.Equal(Start, week.PickedAt);

        var post = Assert.Single(await DraftsAsync());
        Assert.Equal(week.PostId, post.Id);
        Assert.Equal(PostStatuses.Draft, post.Status);
        Assert.Equal("Member spotlight: Ada", post.Title);
        Assert.Empty(post.Destinations);

        await using var context = _db.NewContext();
        Assert.Equal(1, await context.Events.CountAsync(e => e.Type == FactType.PostCreated && e.SubjectId == post.Id.ToString(), Ct));
    }

    [Fact]
    public async Task ABotAMemberWhoLeftAndAMemberWhoDidNotOptInAreNeverPicked()
    {
        await SetUpAsync();

        var picked = new HashSet<string?>();

        // The pool is Ada, Bo and Cy: whichever number the random pick gives, it is one of them.
        foreach (var number in new[] { 0, 1, 2 })
        {
            await SetUpAsync();
            await PassAsync(n => Math.Min(number, n - 1));
            picked.Add(Assert.Single(await WeeksAsync()).DiscordUserId);
        }

        Assert.Equal(new HashSet<string?> { "1", "2", "3" }, picked);
    }

    [Fact]
    public async Task TwoRunsInOneWeekMakeOneDraft()
    {
        await SetUpAsync();

        Assert.True(await PassAsync());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await PassAsync());
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.False(await PassAsync());

        Assert.Single(await WeeksAsync());
        Assert.Single(await DraftsAsync());
    }

    [Fact]
    public async Task TheNextWeekMakesAnotherDraft()
    {
        await SetUpAsync();

        await PassAsync();
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.True(await PassAsync());

        Assert.Equal([ThisWeek, ThisWeek.AddDays(7)], (await WeeksAsync()).Select(w => w.Week));
    }

    [Fact]
    public async Task ATimeAlreadyPastWhenItWasTurnedOnMakesNothing()
    {
        // What saving the settings does: the moment already gone by is marked as dealt with.
        await SetUpAsync(s => s.SpotlightHandledThrough = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));

        Assert.False(await PassAsync());
        Assert.Empty(await DraftsAsync());

        // The next Monday is a new moment.
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.True(await PassAsync());
    }

    [Fact]
    public async Task StaffModeMakesADraftWithNoMemberForStaffToPick()
    {
        await SetUpAsync(s => s.SpotlightMode = SpotlightModes.Staff);

        Assert.True(await PassAsync());

        var week = Assert.Single(await WeeksAsync());
        Assert.Null(week.DiscordUserId);
        Assert.Null(week.PickedAt);

        var post = Assert.Single(await DraftsAsync());
        Assert.Equal(week.PostId, post.Id);
        Assert.Equal(PostStatuses.Draft, post.Status);
        Assert.Equal("Member spotlight", post.Title);
    }

    [Fact]
    public async Task RandomModeSkipsTheWeekWhenNobodyOptedIn()
    {
        await SetUpAsync(optedIn: false);

        Assert.False(await PassAsync());

        Assert.Empty(await WeeksAsync());
        Assert.Empty(await DraftsAsync());

        // The moment is dealt with, so the minute after does not look again.
        await using var context = _db.NewContext();
        Assert.NotNull((await context.GetSettingsAsync(Ct)).SpotlightHandledThrough);
    }

    [Fact]
    public async Task ASiteTickedInSettingsIsOnTheDraft_AndStillNothingIsSent()
    {
        await SetUpAsync(s =>
        {
            s.ManagedGroupId = "grp_00000000-0000-0000-0000-000000000001";
            s.SpotlightPlaces = new TwitchPostPlaces(VRChat: new TwitchVRChatPlace()).Write();
        });

        Assert.True(await PassAsync());

        var post = Assert.Single(await DraftsAsync());
        Assert.Equal(PostStatuses.Draft, post.Status);
        Assert.Equal(PostDestinationStates.Waiting, Assert.Single(post.Destinations).State);
    }

    [Fact]
    public async Task AMemberFeaturedLatelyIsNotPickedAgain()
    {
        await SetUpAsync();

        await using (var context = _db.NewContext())
        {
            // Three members: the last two weeks count as lately, so only Cy is open.
            context.SpotlightWeeks.AddRange(
                new SpotlightWeek { Id = Guid.CreateVersion7(), Week = ThisWeek.AddDays(-7), DiscordUserId = "1", CreatedAt = Start, PickedAt = Start },
                new SpotlightWeek { Id = Guid.CreateVersion7(), Week = ThisWeek.AddDays(-14), DiscordUserId = "2", CreatedAt = Start, PickedAt = Start });
            await context.SaveChangesAsync(Ct);
        }

        Assert.True(await PassAsync(n => n - 1));

        var week = (await WeeksAsync()).Single(w => w.Week == ThisWeek);
        Assert.Equal("3", week.DiscordUserId);
    }
}
