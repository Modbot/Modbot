using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;
using NodaTime;

namespace Modbot.Core.Tests.Profiles;

/// <summary>
/// The weekly member spotlight's rules (spotlight design §4 and §5): who is picked, how many weeks
/// back count as lately, and what the card says. The picking is pure, so it needs no database.
/// </summary>
public class MemberSpotlightTests
{
    private static readonly DateOnly ThisWeek = new(2026, 10, 12);

    private static SpotlightMember Member(string id) => new(id, $"Member {id}", null);

    private static List<SpotlightMember> Pool(int size) => [.. Enumerable.Range(1, size).Select(i => Member(i.ToString()))];

    private static DateOnly Back(int weeks) => ThisWeek.AddDays(-7 * weeks);

    [Fact]
    public void AnEmptyPoolPicksNobody()
    {
        Assert.Null(MemberSpotlight.Pick([], [], ThisWeek, _ => 0));
    }

    [Fact]
    public void ThePickComesFromThePoolOnly()
    {
        var pool = Pool(3);

        for (var i = 0; i < 3; i++)
            Assert.Equal(pool[i].UserId, MemberSpotlight.Pick(pool, [], ThisWeek, _ => i)!.UserId);
    }

    [Fact]
    public void AMemberFeaturedWithinTheWindowIsSkipped()
    {
        var pool = Pool(12);
        var featured = new[] { (Back(1), (string?)"1"), (Back(8), "2") };

        var picks = Enumerable.Range(0, 10).Select(i => MemberSpotlight.Pick(pool, featured, ThisWeek, n => i % n)!.UserId).ToList();

        Assert.DoesNotContain("1", picks);
        Assert.DoesNotContain("2", picks);
    }

    [Fact]
    public void AMemberFeaturedBeforeTheWindowCanComeAgain()
    {
        var pool = Pool(12);
        var featured = new[] { (Back(9), (string?)"1") };

        var picks = Enumerable.Range(0, 12).Select(i => MemberSpotlight.Pick(pool, featured, ThisWeek, _ => i)!.UserId).ToList();

        Assert.Contains("1", picks);
    }

    [Fact]
    public void AWeekWithNoMemberAndThisWeekItselfAreLeftOut()
    {
        var pool = Pool(3);
        var featured = new[] { (Back(1), (string?)null), (ThisWeek, "1") };

        var picks = Enumerable.Range(0, 3).Select(i => MemberSpotlight.Pick(pool, featured, ThisWeek, _ => i)!.UserId).ToList();

        Assert.Equal(["1", "2", "3"], picks);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 4)]
    [InlineData(9, 8)]
    [InlineData(40, 8)]
    public void ASmallPoolShrinksTheWindowSoSomeoneIsLeft(int poolSize, int weeks)
    {
        Assert.Equal(weeks, MemberSpotlight.WeeksBack(poolSize));
    }

    [Fact]
    public void ThreeMembersOnlyRuleOutTheLastTwoWeeks()
    {
        var pool = Pool(3);
        var featured = new[] { (Back(1), (string?)"1"), (Back(2), "2"), (Back(3), "3") };

        // The window is two weeks for three members, so only "3" is open.
        var pick = MemberSpotlight.Pick(pool, featured, ThisWeek, n => n - 1);

        Assert.Equal("3", pick!.UserId);
    }

    [Fact]
    public void ALonelyMemberIsFeaturedAgain()
    {
        var pool = Pool(1);
        var featured = new[] { (Back(1), (string?)"1") };

        Assert.Equal("1", MemberSpotlight.Pick(pool, featured, ThisWeek, _ => 0)!.UserId);
    }

    [Fact]
    public void TheWeekStartsOnMondayInTheZoneGiven()
    {
        var zone = DateTimeZoneProviders.Tzdb["Pacific/Auckland"];

        // Sunday 11 October 2026 22:00 UTC is already Monday 12 October in Auckland.
        var moment = new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 12), MemberSpotlight.WeekOf(moment, zone));
        Assert.Equal(new DateOnly(2026, 10, 5), MemberSpotlight.WeekOf(moment, DateTimeZone.Utc));
    }

    [Fact]
    public void TheCardHasTheNameInTheTitleAndOneLinePerAnswer()
    {
        var lines = new List<ProfileLine>
        {
            new(Guid.NewGuid(), "Hobby", ProfileFieldKind.Text, "Pottery", ProfileVisibility.Members),
            new(Guid.NewGuid(), "Pronouns", ProfileFieldKind.PickList, null, ProfileVisibility.Members),
            new(Guid.NewGuid(), "Favourite world", ProfileFieldKind.Text, "The Great Pug", ProfileVisibility.Members),
        };

        var (title, text) = MemberSpotlight.Card(" Ada ", lines);

        Assert.Equal("Member spotlight: Ada", title);
        Assert.Equal("Hobby: Pottery\nFavourite world: The Great Pug", text);
    }

    [Fact]
    public void ALongTitleAndLongTextAreCutToWhatAPostKeeps()
    {
        var lines = new List<ProfileLine>
        {
            new(Guid.NewGuid(), "Story", ProfileFieldKind.Text, new string('x', Post.MaxTextLength + 50), ProfileVisibility.Members),
        };

        var (title, text) = MemberSpotlight.Card(new string('n', Post.MaxTitleLength + 50), lines);

        Assert.Equal(Post.MaxTitleLength, title.Length);
        Assert.Equal(Post.MaxTextLength, text.Length);
    }
}

/// <summary>The card read from real PostgreSQL: only what the member opened to Members.</summary>
[Collection(nameof(PostgresCollection))]
public class MemberSpotlightCardTests
{
    private const string Ada = "1001";
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public MemberSpotlightCardTests(PostgresFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCardHoldsOnlyTheAnswersOpenedToMembers()
    {
        var connectionString = await _fixture.CreateMigratedDatabaseAsync(Ct);

        try
        {
            await using var db = new ModbotContext(new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(connectionString).Options);

            var open = new ProfileField { Name = "Hobby", Kind = ProfileFieldKind.Text, DefaultVisibility = ProfileVisibility.Members, SortOrder = 0, CreatedAt = At, UpdatedAt = At };
            var staff = new ProfileField { Name = "Pronouns", Kind = ProfileFieldKind.Text, DefaultVisibility = ProfileVisibility.Staff, SortOrder = 1, CreatedAt = At, UpdatedAt = At };
            var closed = new ProfileField { Name = "Time zone", Kind = ProfileFieldKind.Text, DefaultVisibility = ProfileVisibility.OnlyMe, SortOrder = 2, CreatedAt = At, UpdatedAt = At };
            db.ProfileFields.AddRange(open, staff, closed);
            await db.SaveChangesAsync(Ct);

            await MemberProfiles.SetAnswersAsync(
                db,
                Ada,
                new Dictionary<Guid, string?> { [open.Id] = "Pottery", [staff.Id] = "she/her", [closed.Id] = "UTC+1" },
                At,
                Ct);

            var (title, text) = await MemberSpotlight.CardAsync(db, "Ada", Ada, Ct);

            Assert.Equal("Member spotlight: Ada", title);
            Assert.Equal("Hobby: Pottery", text);
        }
        finally
        {
            await TestDatabases.DropAsync(_fixture.ConnectionString, connectionString);
        }
    }
}
