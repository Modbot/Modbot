using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Automatic roles: which rules can be saved, and that a saved rule comes back as it went in.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AutomaticRoleSettingsTests
{
    private readonly PostgresFixture _db;

    public AutomaticRoleSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AutomaticRoleInput Rule(
        string? vrchatRole = "grol_mod",
        string? discordRole = null,
        bool wholeTeam = true,
        IReadOnlyList<string>? whoDiscord = null,
        IReadOnlyList<string>? whoVRChat = null,
        bool companionRunning = true,
        bool clipsReady = false,
        bool inGroupInstance = false,
        bool minimumLevel = false,
        bool passedTrial = false,
        string? staffRole = null,
        bool overturned = false,
        int? overturnedMax = null,
        int? overturnedDays = null,
        bool dailyCap = false,
        int? dailyCapMax = null,
        bool missed = false,
        int? missedMax = null,
        int? missedDays = null,
        IReadOnlyList<Guid>? takeBack = null)
        => new(
            null, vrchatRole, discordRole, wholeTeam, whoDiscord, whoVRChat,
            companionRunning, clipsReady, inGroupInstance, minimumLevel, passedTrial, staffRole,
            overturned, overturnedMax, overturnedDays, dailyCap, dailyCapMax, missed, missedMax, missedDays, takeBack);

    [Fact]
    public void ARuleWithOneConditionAndOneRole_CanBeSaved()
        => Assert.Null(AutomaticRoleRules.Problem(Rule(), evidenceStorageSetUp: false));

    [Fact]
    public void NoConditionSwitchedOn_IsRefused()
        => Assert.NotNull(AutomaticRoleRules.Problem(Rule(companionRunning: false), evidenceStorageSetUp: true));

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", "")]
    public void NeitherRole_IsRefused(string? vrchatRole, string? discordRole)
        => Assert.NotNull(AutomaticRoleRules.Problem(Rule(vrchatRole, discordRole), evidenceStorageSetUp: true));

    [Fact]
    public void ADiscordRoleAlone_IsEnough()
        => Assert.Null(AutomaticRoleRules.Problem(Rule(vrchatRole: null, discordRole: "123"), evidenceStorageSetUp: false));

    [Fact]
    public void ClipsReady_NeedsEvidenceStorage()
    {
        var rule = Rule(companionRunning: false, clipsReady: true);

        Assert.NotNull(AutomaticRoleRules.Problem(rule, evidenceStorageSetUp: false));
        Assert.Null(AutomaticRoleRules.Problem(rule, evidenceStorageSetUp: true));
    }

    [Fact]
    public void MissedClips_NeedsEvidenceStorage_AndZeroIsAllowed()
    {
        var rule = Rule(companionRunning: false, missed: true, missedMax: 0, missedDays: 7);

        Assert.NotNull(AutomaticRoleRules.Problem(rule, evidenceStorageSetUp: false));
        Assert.Null(AutomaticRoleRules.Problem(rule, evidenceStorageSetUp: true));
    }

    [Fact]
    public void OtherConditions_DoNotNeedEvidenceStorage()
    {
        var rule = Rule(companionRunning: true, inGroupInstance: true, dailyCap: true, dailyCapMax: 5);

        Assert.Null(AutomaticRoleRules.Problem(rule, evidenceStorageSetUp: false));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(-1, 30)]
    [InlineData(1001, 30)]
    [InlineData(2, 0)]
    [InlineData(2, 366)]
    [InlineData(2, null)]
    public void OverturnedBans_NeedsSensibleNumbers(int? max, int? days)
        => Assert.NotNull(AutomaticRoleRules.Problem(
            Rule(overturned: true, overturnedMax: max, overturnedDays: days), evidenceStorageSetUp: true));

    [Fact]
    public void OverturnedBans_ZeroAndEdgeNumbers_AreAllowed()
        => Assert.Null(AutomaticRoleRules.Problem(
            Rule(overturned: true, overturnedMax: 0, overturnedDays: 365), evidenceStorageSetUp: true));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1001)]
    public void DailyBanCap_NeedsSensibleNumbers(int? max)
        => Assert.NotNull(AutomaticRoleRules.Problem(Rule(dailyCap: true, dailyCapMax: max), evidenceStorageSetUp: true));

    [Fact]
    public void MinimumLevel_NeedsTheTrialOrAStaffRole()
    {
        Assert.NotNull(AutomaticRoleRules.Problem(Rule(minimumLevel: true), evidenceStorageSetUp: true));
        Assert.Null(AutomaticRoleRules.Problem(Rule(minimumLevel: true, passedTrial: true), evidenceStorageSetUp: true));
        Assert.Null(AutomaticRoleRules.Problem(Rule(minimumLevel: true, staffRole: "555"), evidenceStorageSetUp: true));
    }

    [Fact]
    public void ChosenRoles_NeedAtLeastOneRole_AndTheWholeTeamNeedsNone()
    {
        Assert.NotNull(AutomaticRoleRules.Problem(Rule(wholeTeam: false), evidenceStorageSetUp: true));
        Assert.Null(AutomaticRoleRules.Problem(Rule(wholeTeam: false, whoDiscord: ["900"]), evidenceStorageSetUp: true));
        Assert.Null(AutomaticRoleRules.Problem(Rule(wholeTeam: false, whoVRChat: ["grol_lead"]), evidenceStorageSetUp: true));
        Assert.Null(AutomaticRoleRules.Problem(Rule(wholeTeam: true), evidenceStorageSetUp: true));
    }

    [Fact]
    public void Apply_DropsNumbersOfSwitchedOffConditions_AndTidiesText()
    {
        var row = new AutomaticRole();
        AutomaticRoleRules.Apply(row, Rule(
            vrchatRole: " grol_mod ", discordRole: " ", wholeTeam: true, whoDiscord: ["900"],
            overturned: false, overturnedMax: 3, overturnedDays: 30,
            dailyCap: true, dailyCapMax: 4));

        Assert.Equal("grol_mod", row.VRChatRoleId);
        Assert.Null(row.DiscordRoleId);
        Assert.Empty(row.WhoDiscordRoleIds);
        Assert.Null(row.OverturnedBansMax);
        Assert.Null(row.OverturnedBansDays);
        Assert.Equal(4, row.DailyBanCapMax);
    }

    [Fact]
    public async Task ASavedRule_ComesBackAsItWentIn()
    {
        var person = Guid.NewGuid();
        var id = Guid.CreateVersion7();

        await using (var context = _db.NewContext())
        {
            var row = new AutomaticRole { Id = id, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };
            AutomaticRoleRules.Apply(row, Rule(
                discordRole: "321", wholeTeam: false, whoDiscord: ["900", "901"], whoVRChat: ["grol_lead"],
                clipsReady: true, missed: true, missedMax: 0, missedDays: 14, takeBack: [person]));
            context.AutomaticRoles.Add(row);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = _db.NewContext())
        {
            var row = await context.AutomaticRoles.AsNoTracking().FirstAsync(r => r.Id == id, Ct);

            Assert.False(row.WholeTeam);
            Assert.Equal(["900", "901"], row.WhoDiscordRoleIds);
            Assert.Equal(["grol_lead"], row.WhoVRChatRoleIds);
            Assert.True(row.ClipsReady);
            Assert.Equal(0, row.MissedClipsMax);
            Assert.Equal(14, row.MissedClipsDays);
            Assert.Equal([person], row.TakeBackUserIds);

            context.AutomaticRoles.Remove(await context.AutomaticRoles.FirstAsync(r => r.Id == id, Ct));
            await context.SaveChangesAsync(Ct);
        }
    }
}
