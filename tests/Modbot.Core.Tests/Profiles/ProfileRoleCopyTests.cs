using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Profiles;

/// <summary>
/// Member profiles design §8: "Copy from roles" fills only empty answers, and the Findable
/// switch is off until the member turns it on. Against real PostgreSQL, in a database of its own.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ProfileRoleCopyTests
{
    private const string Guild = "9000";
    private const string SheRole = "111";
    private const string HeRole = "222";
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public ProfileRoleCopyTests(PostgresFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Setup : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _admin;

        public Setup(string connectionString, string admin)
        {
            _connectionString = connectionString;
            _admin = admin;
        }

        public Guid Pronouns { get; set; }
        public Guid She { get; set; }
        public Guid He { get; set; }

        public ModbotContext NewContext()
            => new(new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(_connectionString).Options);

        public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_admin, _connectionString);
    }

    /// <summary>Pronouns (pick-list, Staff): she/her is linked to one role and he/him to another, she/her first.</summary>
    private async Task<Setup> CreateAsync()
    {
        var setup = new Setup(await _fixture.CreateMigratedDatabaseAsync(Ct), _fixture.ConnectionString);
        var she = new ProfileOption { Label = "she/her", SortOrder = 0, DiscordRoleId = SheRole };
        var he = new ProfileOption { Label = "he/him", SortOrder = 1, DiscordRoleId = HeRole };

        await using var db = setup.NewContext();
        var pronouns = new ProfileField
        {
            Name = "Pronouns",
            Kind = ProfileFieldKind.PickList,
            DefaultVisibility = ProfileVisibility.Staff,
            CreatedAt = At,
            UpdatedAt = At,
            Options = [she, he],
        };
        db.ProfileFields.Add(pronouns);
        await db.SaveChangesAsync(Ct);

        setup.Pronouns = pronouns.Id;
        setup.She = she.Id;
        setup.He = he.Id;
        return setup;
    }

    private static async Task AddMemberAsync(ModbotContext db, string userId, string rolesJson, bool left = false, bool bot = false)
    {
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = userId,
            Username = $"user{userId}",
            DisplayName = $"User {userId}",
            IsBot = bot,
            Roles = rolesJson,
            LeftAt = left ? At : null,
            FirstSeenAt = At,
            UpdatedAt = At,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<ProfileValue?> ValueOfAsync(ModbotContext db, Guid field, string userId)
        => await db.ProfileValues.AsNoTracking().FirstOrDefaultAsync(v => v.FieldId == field && v.DiscordUserId == userId, Ct);

    [Fact]
    public async Task TheCopyFillsMembersWhoHoldALinkedRoleAndCountsThem()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AddMemberAsync(db, "1", $"[\"{SheRole}\", \"5\"]");
        await AddMemberAsync(db, "2", $"[\"{HeRole}\"]");
        await AddMemberAsync(db, "3", "[\"5\"]");
        await AddMemberAsync(db, "4", $"[\"{SheRole}\"]", left: true);
        await AddMemberAsync(db, "5", $"[\"{SheRole}\"]", bot: true);

        var filled = await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct);

        Assert.Equal(2, filled);
        Assert.Equal(setup.She, (await ValueOfAsync(db, setup.Pronouns, "1"))!.OptionId);
        Assert.Equal(setup.He, (await ValueOfAsync(db, setup.Pronouns, "2"))!.OptionId);
        Assert.Null(await ValueOfAsync(db, setup.Pronouns, "3"));
        Assert.Null(await ValueOfAsync(db, setup.Pronouns, "4"));
        Assert.Null(await ValueOfAsync(db, setup.Pronouns, "5"));
    }

    [Fact]
    public async Task TheCopyNeverOverwritesAnAnswerTheMemberSet()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AddMemberAsync(db, "1", $"[\"{SheRole}\"]");
        await MemberProfiles.SetAnswersAsync(db, "1", new Dictionary<Guid, string?> { [setup.Pronouns] = setup.He.ToString() }, At, Ct);

        var filled = await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct);

        Assert.Equal(0, filled);
        Assert.Equal(setup.He, (await ValueOfAsync(db, setup.Pronouns, "1"))!.OptionId);
    }

    [Fact]
    public async Task AMemberWithRolesOfTwoOptionsGetsTheFirstOptionInTheFieldsOrder()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();

        // The roles are listed he-first on the member; the field's order still puts she/her first.
        await AddMemberAsync(db, "1", $"[\"{HeRole}\", \"{SheRole}\"]");

        Assert.Equal(1, await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct));
        Assert.Equal(setup.She, (await ValueOfAsync(db, setup.Pronouns, "1"))!.OptionId);
    }

    [Fact]
    public async Task ACopiedAnswerFollowsTheFieldsDefaultAndKeepsALevelTheMemberChose()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AddMemberAsync(db, "1", $"[\"{SheRole}\"]");
        await AddMemberAsync(db, "2", $"[\"{SheRole}\"]");

        // Member 2 only chose who sees the field so far; they have no answer.
        await MemberProfiles.SetVisibilityAsync(db, "2", new Dictionary<Guid, ProfileVisibility> { [setup.Pronouns] = ProfileVisibility.Members }, At, Ct);

        Assert.Equal(2, await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct));
        Assert.Null((await ValueOfAsync(db, setup.Pronouns, "1"))!.Visibility);
        Assert.Equal(ProfileVisibility.Members, (await ValueOfAsync(db, setup.Pronouns, "2"))!.Visibility);
        Assert.Equal(setup.She, (await ValueOfAsync(db, setup.Pronouns, "2"))!.OptionId);
    }

    [Fact]
    public async Task RunningTheCopyAgainFillsNothingMore()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AddMemberAsync(db, "1", $"[\"{SheRole}\"]");

        Assert.Equal(1, await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct));
        Assert.Equal(0, await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct));
    }

    [Fact]
    public async Task AFieldWithNoLinkedRolesOrNoSuchFieldFillsNothing()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AddMemberAsync(db, "1", $"[\"{SheRole}\"]");

        foreach (var option in await db.ProfileOptions.ToListAsync(Ct))
            option.DiscordRoleId = null;

        await db.SaveChangesAsync(Ct);

        Assert.Equal(0, await MemberProfiles.CopyFromRolesAsync(db, setup.Pronouns, At, Ct));
        Assert.Null(await MemberProfiles.CopyFromRolesAsync(db, Guid.NewGuid(), At, Ct));
    }

    [Fact]
    public async Task FindableIsOffUntilTheMemberTurnsItOnAndOffAgainStoresNothing()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();

        Assert.False(await MemberProfiles.IsFindableAsync(db, "1", Ct));

        await MemberProfiles.SetFindableAsync(db, "1", true, At, Ct);
        Assert.True(await MemberProfiles.IsFindableAsync(db, "1", Ct));
        Assert.False(await MemberProfiles.IsFindableAsync(db, "2", Ct));

        // Turning it on twice keeps one row.
        await MemberProfiles.SetFindableAsync(db, "1", true, At, Ct);
        Assert.Equal(1, await db.MemberProfiles.CountAsync(Ct));

        await MemberProfiles.SetFindableAsync(db, "1", false, At, Ct);
        Assert.False(await MemberProfiles.IsFindableAsync(db, "1", Ct));
        Assert.Equal(0, await db.MemberProfiles.CountAsync(Ct));
    }
}
