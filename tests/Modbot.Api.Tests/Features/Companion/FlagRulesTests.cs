using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// What makes a person Flagged, and the settings card that says which rules count (flagged rules
/// design §2-3).
/// </summary>
/// <remarks>
/// Against real PostgreSQL, because the reader is one grouped query per rule and the thing worth
/// proving is that those queries count what they claim to.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class FlagRulesTests
{
    private const string Path = "/api/settings/flag-rules";

    private readonly PostgresFixture _db;

    public FlagRulesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ReadSurfaceTestHost> ReadyAsync()
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // AutoMod's tables are not the read surface's to clear, and a flag left over from another
        // test would flag somebody here.
        await using var context = _db.NewContext();
        await context.ModerationFlags.ExecuteDeleteAsync(Ct);
        await context.ModerationTermLists.ExecuteDeleteAsync(Ct);
        await context.ModerationTopics.ExecuteDeleteAsync(Ct);

        return host;
    }

    private static async Task<IReadOnlyDictionary<string, FlagMatch>> ReadAsync(
        ReadSurfaceTestHost host,
        IReadOnlyDictionary<string, TrustRank?>? ranks,
        params string[] people)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await FlagRules.ReadAsync(
            db, people, ranks ?? new Dictionary<string, TrustRank?>(StringComparer.Ordinal), Ct);
    }

    private static async Task SetRulesAsync(ReadSurfaceTestHost host, FlagRuleSettings rules)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.FlagRules = rules.ToJson();
        await db.SaveChangesAsync(Ct);
    }

    private static async Task WarnAsync(ReadSurfaceTestHost host, string person, int times)
    {
        for (var i = 0; i < times; i++)
            await host.WriteFactAsync(AuditFact(FactType.GroupInstanceWarn, person, host.Clock.UtcNow.AddDays(-1 - i), actor: "usr_mod"), Ct);
    }

    private static async Task<Guid> AddListAsync(ReadSurfaceTestHost host, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var list = new ModerationTermList
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
        };

        db.ModerationTermLists.Add(list);
        await db.SaveChangesAsync(Ct);

        return list.Id;
    }

    private static async Task FlagAsync(
        ReadSurfaceTestHost host,
        Guid ruleId,
        string ruleName,
        string subject,
        FactPlatform platform = FactPlatform.VRChat,
        ModerationFlagState state = ModerationFlagState.Open,
        bool trial = false)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.ModerationFlags.Add(new ModerationFlag
        {
            Id = Guid.NewGuid(),
            FlaggedAt = host.Clock.UtcNow.AddHours(-1),
            RuleKind = ModerationRuleKind.TermList,
            RuleId = ruleId,
            RuleName = ruleName,
            RuleVersion = 1,
            TermKey = "t1",
            Term = "bad",
            Target = "vrchatDisplayName",
            SubjectPlatform = platform,
            SubjectId = subject,
            Matched = "bad",
            State = state,
            Trial = trial,
        });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task KicksAndBans_FlagByDefault_WithTheCountInTheReason()
    {
        await using var host = await ReadyAsync();

        await host.WriteFactAsync(AuditFact(FactType.MemberKicked, "usr_p", host.Clock.UtcNow.AddDays(-3)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_p", host.Clock.UtcNow.AddDays(-2)), Ct);

        var match = (await ReadAsync(host, null, "usr_p"))["usr_p"];

        Assert.True(match.IsFlagged);
        Assert.Equal(2, match.PriorActions);
        Assert.Equal("2 kicks or bans", match.Reason);
    }

    [Fact]
    public async Task KicksAndBansSwitchedOff_StillCarryTheCount_ButDoNotFlag()
    {
        await using var host = await ReadyAsync();
        await SetRulesAsync(host, new FlagRuleSettings { KicksAndBans = false });

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_p", host.Clock.UtcNow.AddDays(-2)), Ct);

        var match = (await ReadAsync(host, null, "usr_p"))["usr_p"];

        Assert.False(match.IsFlagged);
        Assert.Equal(1, match.PriorActions);
    }

    [Fact]
    public async Task Warns_FlagAtTheThreshold_AndNotOneBelowIt()
    {
        await using var host = await ReadyAsync();

        await WarnAsync(host, "usr_four", 4);
        await WarnAsync(host, "usr_five", 5);

        var matches = await ReadAsync(host, null, "usr_four", "usr_five");

        Assert.False(matches["usr_four"].IsFlagged);
        Assert.Equal("5 warns", matches["usr_five"].Reason);

        // Warns are not kicks or bans, and must not leak into the number the wire has always
        // called prior actions.
        Assert.Equal(0, matches["usr_five"].PriorActions);
    }

    [Fact]
    public async Task Warns_FollowTheNumberSet_AndCanBeSwitchedOff()
    {
        await using var host = await ReadyAsync();
        await WarnAsync(host, "usr_p", 2);

        await SetRulesAsync(host, new FlagRuleSettings { WarnsAtLeast = 2 });
        Assert.Equal("2 warns", (await ReadAsync(host, null, "usr_p"))["usr_p"].Reason);

        await SetRulesAsync(host, new FlagRuleSettings { Warns = false, WarnsAtLeast = 2 });
        Assert.False((await ReadAsync(host, null, "usr_p"))["usr_p"].IsFlagged);
    }

    [Fact]
    public async Task Nuisance_Flags_AndAnUnreadRankDoesNot()
    {
        await using var host = await ReadyAsync();

        var ranks = new Dictionary<string, TrustRank?>(StringComparer.Ordinal)
        {
            ["usr_troll"] = TrustRank.Nuisance,
            ["usr_known"] = TrustRank.KnownUser,
        };

        var matches = await ReadAsync(host, ranks, "usr_troll", "usr_known", "usr_unread");

        Assert.Equal("Nuisance", matches["usr_troll"].Reason);
        Assert.False(matches["usr_known"].IsFlagged);
        Assert.False(matches["usr_unread"].IsFlagged);

        await SetRulesAsync(host, new FlagRuleSettings { Nuisance = false });
        Assert.False((await ReadAsync(host, ranks, "usr_troll"))["usr_troll"].IsFlagged);
    }

    [Fact]
    public async Task AutoMod_AnOpenOrConfirmedFlagCounts_ADismissedOrTrialOneDoesNot()
    {
        await using var host = await ReadyAsync();
        var list = await AddListAsync(host, "Slurs");

        await FlagAsync(host, list, "Slurs", "usr_open");
        await FlagAsync(host, list, "Slurs", "usr_confirmed", state: ModerationFlagState.Confirmed);
        await FlagAsync(host, list, "Slurs", "usr_dismissed", state: ModerationFlagState.Dismissed);
        await FlagAsync(host, list, "Slurs", "usr_trial", trial: true);

        var matches = await ReadAsync(host, null, "usr_open", "usr_confirmed", "usr_dismissed", "usr_trial");

        Assert.Equal("AutoMod: Slurs", matches["usr_open"].Reason);
        Assert.Equal("AutoMod: Slurs", matches["usr_confirmed"].Reason);
        Assert.False(matches["usr_dismissed"].IsFlagged);
        Assert.False(matches["usr_trial"].IsFlagged);
    }

    [Fact]
    public async Task AutoMod_OnlyThePickedRulesCount()
    {
        await using var host = await ReadyAsync();
        var slurs = await AddListAsync(host, "Slurs");
        var spam = await AddListAsync(host, "Spam");

        await FlagAsync(host, slurs, "Slurs", "usr_p");
        await FlagAsync(host, spam, "Spam", "usr_p");

        Assert.Equal("AutoMod: Slurs, Spam", (await ReadAsync(host, null, "usr_p"))["usr_p"].Reason);

        await SetRulesAsync(host, new FlagRuleSettings { AutoModRules = [spam] });
        Assert.Equal("AutoMod: Spam", (await ReadAsync(host, null, "usr_p"))["usr_p"].Reason);

        await SetRulesAsync(host, new FlagRuleSettings { AutoModRules = [] });
        Assert.False((await ReadAsync(host, null, "usr_p"))["usr_p"].IsFlagged);
    }

    [Fact]
    public async Task AutoMod_AFlagOnALinkedDiscordAccountCounts_AndAnEndedLinkDoesNot()
    {
        await using var host = await ReadyAsync();
        var list = await AddListAsync(host, "Slurs");

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "111", DiscordUsername = "linked", VRChatUserId = "usr_linked", LinkedAt = host.Clock.UtcNow.AddDays(-5),
            });
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "222", DiscordUsername = "ended", VRChatUserId = "usr_ended", LinkedAt = host.Clock.UtcNow.AddDays(-5),
                UnlinkedAt = host.Clock.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync(Ct);
        }

        await FlagAsync(host, list, "Slurs", "111", FactPlatform.Discord);
        await FlagAsync(host, list, "Slurs", "222", FactPlatform.Discord);

        var matches = await ReadAsync(host, null, "usr_linked", "usr_ended");

        Assert.Equal("AutoMod: Slurs", matches["usr_linked"].Reason);
        Assert.False(matches["usr_ended"].IsFlagged);
    }

    [Fact]
    public async Task SeveralRules_AreListedInTheCardsOrder()
    {
        await using var host = await ReadyAsync();
        var list = await AddListAsync(host, "Slurs");

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_p", host.Clock.UtcNow.AddDays(-9)), Ct);
        await WarnAsync(host, "usr_p", 5);
        await FlagAsync(host, list, "Slurs", "usr_p");

        var match = (await ReadAsync(
            host,
            new Dictionary<string, TrustRank?>(StringComparer.Ordinal) { ["usr_p"] = TrustRank.Nuisance },
            "usr_p"))["usr_p"];

        Assert.Equal(new[] { "1 kick or ban", "5 warns", "Nuisance", "AutoMod: Slurs" }, match.Reasons);
        Assert.Equal("1 kick or ban · 5 warns · Nuisance · AutoMod: Slurs", match.Reason);
    }

    [Fact]
    public void AMissingOrBrokenSettingsDocument_IsEveryDefault()
    {
        Assert.Equal(FlagRuleSettings.Default, FlagRuleSettings.Read(null));
        Assert.Equal(FlagRuleSettings.Default, FlagRuleSettings.Read("{not json"));

        var sparse = FlagRuleSettings.Read("""{"warnsAtLeast":3}""");
        Assert.Equal(3, sparse.WarnsAtLeast);
        Assert.True(sparse.KicksAndBans && sparse.Warns && sparse.Nuisance && sparse.AutoMod);
        Assert.Null(sparse.AutoModRules);

        Assert.Equal(FlagRuleSettings.MaxWarns, FlagRuleSettings.Read("""{"warnsAtLeast":5000}""").WarnsAtLeast);
    }

    [Fact]
    public async Task TheCard_WithoutManageSettings_IsRefused()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Path, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheCard_ShowsTheDefaults_AndSavesWhatItIsSent()
    {
        await using var host = await ReadyAsync();
        var spam = await AddListAsync(host, "Spam");
        var slurs = await AddListAsync(host, "Slurs");

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var view = await host.GetJsonAsync<FlagRulesView>(Path, cookie, Ct);
        Assert.True(view.KicksAndBans && view.Warns && view.Nuisance && view.AutoMod && view.EveryAutoModRule);
        Assert.Equal(5, view.WarnsAtLeast);
        Assert.Equal(new[] { "Slurs", "Spam" }, view.AutoModRules.Select(r => r.Name));
        Assert.All(view.AutoModRules, r => Assert.True(r.Counts));

        var response = await host.PutJsonAsync(
            Path,
            new
            {
                kicksAndBans = true,
                warns = true,
                warnsAtLeast = 3,
                nuisance = false,
                autoMod = true,
                everyAutoModRule = false,
                autoModRules = new[] { spam },
            },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = (await response.Content.ReadFromJsonAsync<FlagRulesView>(Ct))!;
        Assert.Equal(3, saved.WarnsAtLeast);
        Assert.False(saved.Nuisance);
        Assert.False(saved.EveryAutoModRule);
        Assert.True(saved.AutoModRules.Single(r => r.Id == spam).Counts);
        Assert.False(saved.AutoModRules.Single(r => r.Id == slurs).Counts);

        // The change is on the record, the way every other settings change is.
        await using var context = _db.NewContext();
        var changes = await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.SettingsChanged)
            .Select(e => e.Data)
            .ToListAsync(Ct);
        Assert.Contains(changes, d => d is not null && d.Contains("\"flagRules\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCard_RefusesAWarnCountOutOfRange_AndARuleThatDoesNotExist()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        object Body(int warnsAtLeast, Guid[] rules) => new
        {
            kicksAndBans = true,
            warns = true,
            warnsAtLeast,
            nuisance = true,
            autoMod = true,
            everyAutoModRule = rules.Length == 0,
            autoModRules = rules,
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync(Path, Body(0, []), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync(Path, Body(100, []), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync(Path, Body(5, [Guid.NewGuid()]), cookie, Ct)).StatusCode);
    }
}
