using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Birthdays;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Birthdays;

/// <summary>
/// The daily birthday pass: one post a day however often it runs or restarts, a day that could not be
/// tried is tried again, members who left are skipped, the role is given for the day and taken back
/// the next, a role with a dangerous permission is never given, 29 February is kept on 28 February in
/// a year with no 29th, and a row left with a role to take back is cleared by the pass.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BirthdayPassTests : IAsyncLifetime
{
    private const string Guild = "700";
    private const string Channel = "900";
    private const string Party = "811";
    private const string Admin = "812";

    private const long Community = (1L << 10) | (1L << 11);
    private const long Administrator = 1L << 3;

    private readonly PostgresFixture _fixture;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeActions _discord = new();
    private string _connectionString = string.Empty;

    public BirthdayPassTests(PostgresFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await _fixture.CreateMigratedDatabaseAsync(Ct);

        await using var db = NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;
        settings.BirthdaysOn = true;
        settings.BirthdayChannelId = Channel;

        var at = _clock.UtcNow;
        db.DiscordRoles.AddRange(
            Role(Party, "Birthday", Community, at),
            Role(Admin, "Boss", Community | Administrator, at));

        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
        => await TestDatabases.DropAsync(_fixture.ConnectionString, _connectionString);

    private ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(_connectionString).Options;
        return new ModbotContext(options);
    }

    private sealed class FakeActions : IDiscordBirthdayActions
    {
        public List<(string ChannelId, string Text)> Posts { get; } = [];

        public List<(string UserId, string RoleId, bool Add)> RoleChanges { get; } = [];

        public DiscordBirthdayOutcome PostResult { get; set; } = DiscordBirthdayOutcome.Ok;

        public DiscordBirthdayOutcome RoleResult { get; set; } = DiscordBirthdayOutcome.Ok;

        public HashSet<string> NotInServer { get; } = [];

        /// <summary>When set, taking the role back answers this; giving it still answers <see cref="RoleResult"/>.</summary>
        public DiscordBirthdayOutcome? TakeBackResult { get; set; }

        /// <summary>Members Discord refuses to take the role back from.</summary>
        public HashSet<string> RefuseTakeBackFor { get; } = [];

        public Task<DiscordBirthdayOutcome> PostAsync(string channelId, string text, CancellationToken ct = default)
        {
            if (PostResult.Done)
                Posts.Add((channelId, text));

            return Task.FromResult(PostResult);
        }

        public Task<DiscordBirthdayOutcome> ChangeRoleAsync(
            string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
        {
            if (NotInServer.Contains(userId))
                return Task.FromResult(DiscordBirthdayOutcome.Left);

            if (!add && RefuseTakeBackFor.Contains(userId))
                return Task.FromResult(DiscordBirthdayOutcome.Failed("Missing permissions."));

            var result = !add && TakeBackResult is { } takeBack ? takeBack : RoleResult;

            if (result.Done)
                RoleChanges.Add((userId, roleId, add));

            return Task.FromResult(result);
        }
    }

    private static DiscordRole Role(string id, string name, long permissions, DateTimeOffset at) => new()
    {
        RoleId = id,
        GuildId = Guild,
        Name = name,
        Permissions = permissions,
        BotCanAssign = true,
        FirstSeenAt = at,
        UpdatedAt = at,
    };

    /// <summary>A pass as it would be made after a restart: its own context, the shared fakes.</summary>
    private async Task<bool> RunAsync()
    {
        await using var db = NewContext();
        return await new BirthdayPass(db, _clock, _discord).RunOnceAsync(Ct);
    }

    private async Task AddBirthdaysAsync(int month, int day, params string[] users)
    {
        await using var db = NewContext();

        foreach (var user in users)
            db.MemberBirthdays.Add(new MemberBirthday { DiscordUserId = user, Month = month, Day = day, SetAt = _clock.UtcNow });

        await db.SaveChangesAsync(Ct);
    }

    private async Task ConfigureAsync(Action<Core.Data.Entities.Settings> change)
    {
        await using var db = NewContext();
        change(await db.GetSettingsAsync(Ct));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<MemberBirthday> RowAsync(string user)
    {
        await using var db = NewContext();
        return await db.MemberBirthdays.AsNoTracking().SingleAsync(b => b.DiscordUserId == user, Ct);
    }

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var db = NewContext();
        return await db.Settings.AsNoTracking().SingleAsync(Ct);
    }

    [Fact]
    public async Task ThatDaysBirthdays_ArePostedOnce_HoweverOftenThePassRunsAndAfterARestart()
    {
        await AddBirthdaysAsync(10, 13, "1", "2");
        await AddBirthdaysAsync(10, 14, "3");

        Assert.True(await RunAsync());
        Assert.False(await RunAsync());
        Assert.False(await RunAsync());

        // A new context and pass each time is a restart.
        _clock.Advance(TimeSpan.FromHours(3));
        Assert.False(await RunAsync());

        var post = Assert.Single(_discord.Posts);
        Assert.Equal(Channel, post.ChannelId);
        Assert.Equal("Happy birthday <@1>, <@2>!", post.Text);
    }

    [Fact]
    public async Task TheNextDay_PostsAgain_ForThatDaysMembers()
    {
        await AddBirthdaysAsync(10, 13, "1");
        await AddBirthdaysAsync(10, 14, "2");

        await RunAsync();
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await RunAsync());

        Assert.Equal(["Happy birthday <@1>!", "Happy birthday <@2>!"], _discord.Posts.Select(p => p.Text));
    }

    [Fact]
    public async Task BeforeTheHour_NothingIsPostedForTheDayThatHasNotStarted()
    {
        await AddBirthdaysAsync(10, 13, "1");
        await ConfigureAsync(s => s.BirthdayHour = 18);

        // At 10:00 the latest 18:00 was yesterday's, which was no one's birthday.
        Assert.False(await RunAsync());
        Assert.Empty(_discord.Posts);

        _clock.Advance(TimeSpan.FromHours(9));
        Assert.True(await RunAsync());
        Assert.Single(_discord.Posts);
    }

    [Fact]
    public async Task TheCustomMessage_IsUsed_WithTheMembersPutIn()
    {
        await ConfigureAsync(s => s.BirthdayMessage = "Cake for {members}");
        await AddBirthdaysAsync(10, 13, "1");

        await RunAsync();

        Assert.Equal("Cake for <@1>", Assert.Single(_discord.Posts).Text);
    }

    [Fact]
    public async Task WithBirthdaysOff_OrNoChannel_NothingIsPosted()
    {
        await AddBirthdaysAsync(10, 13, "1");

        await ConfigureAsync(s => s.BirthdaysOn = false);
        Assert.False(await RunAsync());

        await ConfigureAsync(s =>
        {
            s.BirthdaysOn = true;
            s.BirthdayChannelId = null;
        });
        Assert.False(await RunAsync());

        Assert.Empty(_discord.Posts);
    }

    [Fact]
    public async Task WhenTheBotIsOffline_TheClaimIsGivenBack_AndTheNextPassPosts()
    {
        await AddBirthdaysAsync(10, 13, "1");
        _discord.PostResult = DiscordBirthdayOutcome.Offline;

        Assert.False(await RunAsync());
        Assert.Null((await SettingsAsync()).BirthdayHandledThrough);

        _discord.PostResult = DiscordBirthdayOutcome.Ok;
        Assert.True(await RunAsync());
        Assert.Single(_discord.Posts);
    }

    [Fact]
    public async Task WhenDiscordRateLimits_TheClaimIsGivenBack_AndTheNextPassPosts()
    {
        await AddBirthdaysAsync(10, 13, "1");
        _discord.PostResult = DiscordBirthdayOutcome.Failed("Slow down.", tryAgain: true);

        Assert.False(await RunAsync());

        _discord.PostResult = DiscordBirthdayOutcome.Ok;
        Assert.True(await RunAsync());
        Assert.Single(_discord.Posts);
    }

    [Fact]
    public async Task WhenDiscordRefusesThePost_ItIsNotSentAgain_AndTheProblemIsKept()
    {
        await AddBirthdaysAsync(10, 13, "1");
        _discord.PostResult = DiscordBirthdayOutcome.Failed("Missing access.");

        Assert.False(await RunAsync());
        Assert.Contains("Missing access.", (await SettingsAsync()).BirthdayProblem);

        _discord.PostResult = DiscordBirthdayOutcome.Ok;
        Assert.False(await RunAsync());
        Assert.Empty(_discord.Posts);
    }

    [Fact]
    public async Task AGoodDay_ClearsTheProblem()
    {
        await ConfigureAsync(s => s.BirthdayProblem = "The birthday post failed: Missing access.");
        await AddBirthdaysAsync(10, 13, "1");

        await RunAsync();

        Assert.Null((await SettingsAsync()).BirthdayProblem);
    }

    [Fact]
    public async Task MembersWhoLeftTheServer_AreSkipped()
    {
        await AddBirthdaysAsync(10, 13, "1", "2");
        await using (var db = NewContext())
        {
            db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = Guild,
                UserId = "2",
                Username = "bea",
                DisplayName = "Bea",
                LeftAt = _clock.UtcNow.AddDays(-1),
                FirstSeenAt = _clock.UtcNow.AddDays(-30),
                UpdatedAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await RunAsync();

        Assert.Equal("Happy birthday <@1>!", Assert.Single(_discord.Posts).Text);
    }

    [Fact]
    public async Task WhenNoOneLeftHasABirthday_NothingIsPosted()
    {
        await AddBirthdaysAsync(10, 14, "1");

        Assert.False(await RunAsync());
        Assert.Empty(_discord.Posts);
    }

    [Fact]
    public async Task MoreThanFortyMembers_AreNamedInMoreThanOnePost()
    {
        var users = Enumerable.Range(1, BirthdayRules.MostPerPost + 5).Select(i => (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await AddBirthdaysAsync(10, 13, users);

        await RunAsync();

        Assert.Equal(2, _discord.Posts.Count);
        Assert.All(users, user => Assert.Single(_discord.Posts, p => p.Text.Contains($"<@{user}>", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheRole_IsGivenForTheDay_AndTakenBackOnTheNext()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1");

        await RunAsync();

        Assert.Equal(("1", Party, true), Assert.Single(_discord.RoleChanges));
        var given = await RowAsync("1");
        Assert.Equal(Party, given.RoleGivenId);
        Assert.Equal(new DateOnly(2026, 10, 13), given.RoleGivenOn);

        // The same day, however often: it stays.
        await RunAsync();
        Assert.Single(_discord.RoleChanges);

        _clock.Advance(TimeSpan.FromDays(1));
        await RunAsync();

        Assert.Equal(("1", Party, false), _discord.RoleChanges[^1]);
        Assert.Equal(2, _discord.RoleChanges.Count);
        var taken = await RowAsync("1");
        Assert.Null(taken.RoleGivenId);
        Assert.Null(taken.RoleGivenOn);
    }

    [Fact]
    public async Task TheRole_IsTakenBack_EvenAfterBirthdaysAreTurnedOff_AndAfterAnotherRoleWasChosen()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1");
        await RunAsync();

        await ConfigureAsync(s =>
        {
            s.BirthdaysOn = false;
            s.BirthdayRoleId = null;
        });
        _clock.Advance(TimeSpan.FromDays(1));
        await RunAsync();

        Assert.Equal(("1", Party, false), _discord.RoleChanges[^1]);
    }

    [Fact]
    public async Task ARoleWithADangerousPermission_IsGivenToNobody_ButTheDayIsStillPosted()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Admin);
        await AddBirthdaysAsync(10, 13, "1");

        Assert.True(await RunAsync());

        Assert.Single(_discord.Posts);
        Assert.Empty(_discord.RoleChanges);
        Assert.Contains("Boss", (await SettingsAsync()).BirthdayProblem);
        Assert.Null((await RowAsync("1")).RoleGivenId);
    }

    [Fact]
    public async Task AMemberWhoIsNotInTheServer_IsNotGivenTheRole_AndTheOthersAre()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1", "2");
        _discord.NotInServer.Add("1");

        await RunAsync();

        Assert.Equal(("2", Party, true), Assert.Single(_discord.RoleChanges));
        Assert.Null((await RowAsync("1")).RoleGivenId);
    }

    [Fact]
    public async Task WhenDiscordRefusesTheRole_TheRestAreNotTried_AndTheProblemIsKept()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1", "2");
        _discord.RoleResult = DiscordBirthdayOutcome.Failed("Missing permissions.");

        await RunAsync();

        Assert.Single(_discord.Posts);
        Assert.Contains("Missing permissions.", (await SettingsAsync()).BirthdayProblem);
    }

    [Fact]
    public async Task AMemberWhoSet29February_IsCelebratedOn28FebruaryInAYearWithNo29th()
    {
        _clock.UtcNow = new DateTimeOffset(2027, 2, 28, 10, 0, 0, TimeSpan.Zero);
        await AddBirthdaysAsync(2, 29, "1");

        Assert.True(await RunAsync());
        Assert.Equal("Happy birthday <@1>!", Assert.Single(_discord.Posts).Text);

        // And not again on 1 March.
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.False(await RunAsync());
        Assert.Single(_discord.Posts);
    }

    [Fact]
    public async Task AMemberWhoSet29February_IsCelebratedOn29FebruaryInALeapYear_AndNotThe28th()
    {
        _clock.UtcNow = new DateTimeOffset(2028, 2, 28, 10, 0, 0, TimeSpan.Zero);
        await AddBirthdaysAsync(2, 29, "1");

        Assert.False(await RunAsync());

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await RunAsync());
        Assert.Single(_discord.Posts);
    }

    [Fact]
    public async Task TheDay_IsTheOneInTheSettingsTimeZone()
    {
        // 23:30 in Los Angeles on 12 October is 06:30 on 13 October in UTC; with the hour at 6 in
        // Los Angeles the latest moment is 12 October's 06:00 there.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 13, 6, 30, 0, TimeSpan.Zero);
        await ConfigureAsync(s =>
        {
            s.BirthdayTimeZone = "America/Los_Angeles";
            s.BirthdayHour = 6;
        });
        await AddBirthdaysAsync(10, 12, "1");
        await AddBirthdaysAsync(10, 13, "2");

        await RunAsync();

        Assert.Equal("Happy birthday <@1>!", Assert.Single(_discord.Posts).Text);
    }

    [Fact]
    public async Task ARowLeftWithOnlyARoleToTakeBack_HasItTakenBack_ThenIsDeleted_EvenWithBirthdaysOff()
    {
        // What a member removing their birthday, or a purge, leaves while the role was still held.
        await using (var db = NewContext())
        {
            db.MemberBirthdays.Add(new MemberBirthday
            {
                DiscordUserId = "1",
                Month = 0,
                Day = 0,
                SetAt = _clock.UtcNow,
                RoleGivenId = Party,
                RoleGivenOn = new DateOnly(2026, 10, 13),
            });
            await db.SaveChangesAsync(Ct);
        }

        await ConfigureAsync(s => s.BirthdaysOn = false);

        await RunAsync();

        Assert.Equal(("1", Party, false), Assert.Single(_discord.RoleChanges));
        await using var read = NewContext();
        Assert.Empty(await read.MemberBirthdays.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ARowLeftWithARoleToTakeBack_IsKept_WhileTheBotIsOffline()
    {
        await using (var db = NewContext())
        {
            db.MemberBirthdays.Add(new MemberBirthday
            {
                DiscordUserId = "1",
                Month = 0,
                Day = 0,
                SetAt = _clock.UtcNow,
                RoleGivenId = Party,
                RoleGivenOn = new DateOnly(2026, 10, 13),
            });
            await db.SaveChangesAsync(Ct);
        }

        _discord.RoleResult = DiscordBirthdayOutcome.Offline;
        await RunAsync();
        Assert.Equal(Party, (await RowAsync("1")).RoleGivenId);

        _discord.RoleResult = DiscordBirthdayOutcome.Ok;
        await RunAsync();

        await using var read = NewContext();
        Assert.Empty(await read.MemberBirthdays.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ARoleDeletedInDiscord_CountsAsTakenBack_SoOneStuckRowDoesNotBlockTheRest()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1", "2");
        await RunAsync();

        // Discord no longer has the role. Taking it back is answered "gone" for both members.
        _discord.TakeBackResult = DiscordBirthdayOutcome.Gone;
        _clock.Advance(TimeSpan.FromDays(1));
        await RunAsync();

        Assert.Null((await RowAsync("1")).RoleGivenId);
        Assert.Null((await RowAsync("2")).RoleGivenId);
        Assert.Null((await SettingsAsync()).BirthdayProblem);
    }

    [Fact]
    public async Task ARoleDeletedInDiscord_LetsAPlaceholderRowBeDeleted()
    {
        await using (var db = NewContext())
        {
            db.MemberBirthdays.Add(new MemberBirthday
            {
                DiscordUserId = "1",
                Month = 0,
                Day = 0,
                SetAt = _clock.UtcNow,
                RoleGivenId = Party,
                RoleGivenOn = new DateOnly(2026, 10, 13),
            });
            await db.SaveChangesAsync(Ct);
        }

        _discord.TakeBackResult = DiscordBirthdayOutcome.Gone;
        await RunAsync();

        await using var read = NewContext();
        Assert.Empty(await read.MemberBirthdays.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ARefusedTakeBack_KeepsTheRowsAlreadyHandled()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1", "2");
        await RunAsync();

        // "1" is taken back, then Discord refuses "2".
        _discord.TakeBackResult = null;
        _clock.Advance(TimeSpan.FromDays(1));
        _discord.RefuseTakeBackFor.Add("2");
        await RunAsync();

        Assert.Null((await RowAsync("1")).RoleGivenId);
        Assert.Equal(Party, (await RowAsync("2")).RoleGivenId);
        Assert.Contains("not taken back", (await SettingsAsync()).BirthdayProblem);
    }

    [Fact]
    public async Task AGivenRoleThatIsGone_IsAProblemOnTheCard_NotARetryLoop()
    {
        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1", "2");
        _discord.RoleResult = DiscordBirthdayOutcome.Gone;

        await RunAsync();
        await RunAsync();

        Assert.Single(_discord.Posts);
        Assert.Contains("no longer exists", (await SettingsAsync()).BirthdayProblem);
    }

    [Fact]
    public async Task WhenThePermissionsAreNotReadYet_TheRoleIsGivenOnALaterPassThatDay_WithoutPostingAgain()
    {
        await using (var db = NewContext())
        {
            var role = await db.DiscordRoles.SingleAsync(r => r.RoleId == Party, Ct);
            role.Permissions = null;
            await db.SaveChangesAsync(Ct);
        }

        await ConfigureAsync(s => s.BirthdayRoleId = Party);
        await AddBirthdaysAsync(10, 13, "1");

        Assert.True(await RunAsync());
        Assert.Empty(_discord.RoleChanges);
        Assert.Contains("not read", (await SettingsAsync()).BirthdayProblem);

        // Still unread: nothing is tried, the problem stays.
        await RunAsync();
        Assert.Empty(_discord.RoleChanges);

        await using (var db = NewContext())
        {
            var role = await db.DiscordRoles.SingleAsync(r => r.RoleId == Party, Ct);
            role.Permissions = Community;
            await db.SaveChangesAsync(Ct);
        }

        Assert.False(await RunAsync());
        Assert.Equal(("1", Party, true), Assert.Single(_discord.RoleChanges));
        Assert.Single(_discord.Posts);
        Assert.Equal(new DateOnly(2026, 10, 13), (await RowAsync("1")).RoleGivenOn);
        Assert.Null((await SettingsAsync()).BirthdayProblem);

        // And once given, no pass gives it again.
        await RunAsync();
        Assert.Single(_discord.RoleChanges);
    }
}
