using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Profiles;

/// <summary>
/// Member profiles design §3 and §6: who sees which answer, and what a save stores. Against real
/// PostgreSQL, in a database of its own, because the fields are shared by everyone in it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MemberProfilesTests
{
    private const string Ada = "1001";
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public MemberProfilesTests(PostgresFixture fixture) => _fixture = fixture;

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
        public Guid Hobby { get; set; }
        public Guid Zone { get; set; }
        public Guid She { get; set; }

        public ModbotContext NewContext()
            => new(new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(_connectionString).Options);

        public async ValueTask DisposeAsync() => await TestDatabases.DropAsync(_admin, _connectionString);
    }

    /// <summary>Three fields: Pronouns (pick-list, Staff), Hobby (text, Members), Time zone (text, Only me).</summary>
    private async Task<Setup> CreateAsync()
    {
        var setup = new Setup(await _fixture.CreateMigratedDatabaseAsync(Ct), _fixture.ConnectionString);
        var she = new ProfileOption { Label = "she/her", SortOrder = 0 };
        var pronouns = new ProfileField
        {
            Name = "Pronouns",
            Kind = ProfileFieldKind.PickList,
            DefaultVisibility = ProfileVisibility.Staff,
            SortOrder = 0,
            CreatedAt = At,
            UpdatedAt = At,
            Options = [she, new ProfileOption { Label = "he/him", SortOrder = 1 }],
        };
        var hobby = new ProfileField { Name = "Hobby", Kind = ProfileFieldKind.Text, DefaultVisibility = ProfileVisibility.Members, SortOrder = 1, CreatedAt = At, UpdatedAt = At };
        var zone = new ProfileField { Name = "Time zone", Kind = ProfileFieldKind.Text, DefaultVisibility = ProfileVisibility.OnlyMe, SortOrder = 2, CreatedAt = At, UpdatedAt = At };

        await using var db = setup.NewContext();
        db.ProfileFields.AddRange(pronouns, hobby, zone);
        await db.SaveChangesAsync(Ct);

        setup.Pronouns = pronouns.Id;
        setup.Hobby = hobby.Id;
        setup.Zone = zone.Id;
        setup.She = she.Id;
        return setup;
    }

    private static Task AnswerAllAsync(Setup setup, ModbotContext db)
        => MemberProfiles.SetAnswersAsync(
            db,
            Ada,
            new Dictionary<Guid, string?>
            {
                [setup.Pronouns] = setup.She.ToString(),
                [setup.Hobby] = "Pottery",
                [setup.Zone] = "UTC+1",
            },
            At,
            Ct);

    [Fact]
    public async Task TheOwnerSeesEveryFieldAnsweredOrNot()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await MemberProfiles.SetAnswersAsync(db, Ada, new Dictionary<Guid, string?> { [setup.Hobby] = "Pottery" }, At, Ct);

        var lines = await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Owner, Ct);

        Assert.Equal(["Pronouns", "Hobby", "Time zone"], lines.Select(l => l.Name));
        Assert.Null(lines[0].Answer);
        Assert.Equal("Pottery", lines[1].Answer);
    }

    [Fact]
    public async Task StaffSeeEveryAnsweredFieldEvenOnesSetToOnlyMe()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AnswerAllAsync(setup, db);

        var lines = await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Staff, Ct);

        Assert.Equal(["Pronouns", "Hobby", "Time zone"], lines.Select(l => l.Name));
        Assert.Equal(ProfileVisibility.OnlyMe, lines.Single(l => l.Name == "Time zone").Visibility);
        Assert.Equal("she/her", lines[0].Answer);
    }

    [Fact]
    public async Task StaffDoNotSeeAFieldNobodyAnswered()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();

        Assert.Empty(await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Staff, Ct));
    }

    [Fact]
    public async Task AnotherMemberSeesOnlyWhatWasOpenedToMembers()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AnswerAllAsync(setup, db);

        var lines = await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Member, Ct);

        Assert.Equal(["Hobby"], lines.Select(l => l.Name));
    }

    [Fact]
    public async Task AMemberCanOpenAFieldToMembersOrCloseOneToThemselves()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AnswerAllAsync(setup, db);

        await MemberProfiles.SetVisibilityAsync(
            db,
            Ada,
            new Dictionary<Guid, ProfileVisibility>
            {
                [setup.Pronouns] = ProfileVisibility.Members,
                [setup.Hobby] = ProfileVisibility.OnlyMe,
            },
            At,
            Ct);

        var lines = await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Member, Ct);
        Assert.Equal(["Pronouns"], lines.Select(l => l.Name));

        var staff = await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Staff, Ct);
        Assert.Equal(3, staff.Count);
    }

    [Fact]
    public async Task SendingTheLevelsAlreadyInForceStoresNothingSoTheDefaultStillReachesTheMember()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AnswerAllAsync(setup, db);

        await MemberProfiles.SetVisibilityAsync(
            db,
            Ada,
            new Dictionary<Guid, ProfileVisibility>
            {
                [setup.Pronouns] = ProfileVisibility.Staff,
                [setup.Hobby] = ProfileVisibility.Members,
                [setup.Zone] = ProfileVisibility.OnlyMe,
            },
            At,
            Ct);

        await using var read = setup.NewContext();
        Assert.All(await read.ProfileValues.AsNoTracking().ToListAsync(Ct), v => Assert.Null(v.Visibility));

        // The admin changes a default; the member who never chose follows it.
        var field = await read.ProfileFields.SingleAsync(f => f.Id == setup.Hobby, Ct);
        field.DefaultVisibility = ProfileVisibility.Staff;
        await read.SaveChangesAsync(Ct);

        await using var after = setup.NewContext();
        Assert.Empty(await MemberProfiles.LinesAsync(after, Ada, ProfileViewer.Member, Ct));
    }

    [Fact]
    public async Task ChoosingTheLevelOfAnUnansweredFieldKeepsTheChoiceForWhenItIsAnswered()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();

        await MemberProfiles.SetVisibilityAsync(db, Ada, new Dictionary<Guid, ProfileVisibility> { [setup.Pronouns] = ProfileVisibility.Members }, At, Ct);
        await MemberProfiles.SetAnswersAsync(db, Ada, new Dictionary<Guid, string?> { [setup.Pronouns] = setup.She.ToString() }, At, Ct);

        var lines = await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Member, Ct);
        Assert.Contains(lines, l => l.Name == "Pronouns");
    }

    [Fact]
    public async Task ClearingAnAnswerRemovesTheRowWhenNothingElseIsStoredInIt()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();
        await AnswerAllAsync(setup, db);

        await MemberProfiles.SetAnswersAsync(db, Ada, new Dictionary<Guid, string?> { [setup.Hobby] = "  " }, At, Ct);

        await using var read = setup.NewContext();
        Assert.DoesNotContain(await read.ProfileValues.ToListAsync(Ct), v => v.FieldId == setup.Hobby);
    }

    [Fact]
    public async Task AnOptionTheFieldDoesNotHaveAndTextPastTheLimitAreLeftOut()
    {
        await using var setup = await CreateAsync();
        await using var db = setup.NewContext();

        await MemberProfiles.SetAnswersAsync(
            db,
            Ada,
            new Dictionary<Guid, string?>
            {
                [setup.Pronouns] = Guid.NewGuid().ToString(),
                [setup.Hobby] = new string('x', ProfileField.MaxTextLength + 1),
            },
            At,
            Ct);

        Assert.Empty(await MemberProfiles.LinesAsync(db, Ada, ProfileViewer.Staff, Ct));
    }
}
