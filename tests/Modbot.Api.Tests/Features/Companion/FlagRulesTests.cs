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
            db, people, ranks ?? new Dictionary<string, TrustRank?>(StringComparer.Ordinal), host.Clock.UtcNow, Ct);
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

    private static async Task<PersonWatch> WatchAsync(
        ReadSurfaceTestHost host,
        string subject,
        string reason,
        FactPlatform platform = FactPlatform.VRChat,
        DateTimeOffset? endsAt = null,
        DateTimeOffset? endedAt = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var watch = new PersonWatch
        {
            SubjectPlatform = platform,
            SubjectId = subject,
            Reason = reason,
            SetByUserId = Guid.NewGuid(),
            SetByUsername = "mira",
            SetAt = host.Clock.UtcNow.AddDays(-1),
            EndsAt = endsAt,
            EndedAt = endedAt,
        };

        db.PersonWatches.Add(watch);
        await db.SaveChangesAsync(Ct);

        return watch;
    }

    [Fact]
    public async Task AWatch_Flags_ListedFirst_AsTheWordAloneWithoutItsReason()
    {
        await using var host = await ReadyAsync();

        var watch = await WatchAsync(host, "usr_p", "Said they would come back with alts");
        await host.WriteFactAsync(AuditFact(FactType.MemberKicked, "usr_p", host.Clock.UtcNow.AddDays(-3)), Ct);

        var match = (await ReadAsync(host, null, "usr_p"))["usr_p"];

        Assert.True(match.IsFlagged);
        Assert.Equal(new[] { "Watched", "1 kick or ban" }, match.Reasons);
        Assert.Equal(watch.Id, match.Watch?.Id);

        // The reasons reach a paired companion, the Live page and the chat tools, none of which
        // needs the audit log's permission; the watch's reason is that log's to show.
        Assert.DoesNotContain("alts", match.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWatch_PastItsEndDay_OrStopped_DoesNotFlag()
    {
        await using var host = await ReadyAsync();

        await WatchAsync(host, "usr_ran_out", "x", endsAt: host.Clock.UtcNow.AddMinutes(-1));
        await WatchAsync(host, "usr_stopped", "x", endedAt: host.Clock.UtcNow.AddHours(-1));
        await WatchAsync(host, "usr_still", "x", endsAt: host.Clock.UtcNow.AddDays(1));

        var matches = await ReadAsync(host, null, "usr_ran_out", "usr_stopped", "usr_still");

        Assert.False(matches["usr_ran_out"].IsFlagged);
        Assert.Null(matches["usr_ran_out"].Watch);
        Assert.False(matches["usr_stopped"].IsFlagged);
        Assert.True(matches["usr_still"].IsFlagged);

        // The same watch, read a day and a minute later, has run out.
        host.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.False((await ReadAsync(host, null, "usr_still"))["usr_still"].IsFlagged);
    }

    [Fact]
    public async Task AWatch_OnALinkedDiscordAccount_FlagsTheVRChatPerson()
    {
        await using var host = await ReadyAsync();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = "333", DiscordUsername = "linked", VRChatUserId = "usr_linked", LinkedAt = host.Clock.UtcNow.AddDays(-5),
            });
            await db.SaveChangesAsync(Ct);
        }

        await WatchAsync(host, "333", "Raid in the Discord", FactPlatform.Discord);

        var match = (await ReadAsync(host, null, "usr_linked"))["usr_linked"];

        Assert.Equal("Watched", match.Reason);
        Assert.Equal(FactPlatform.Discord, match.Watch?.Platform);
    }

    [Fact]
    public async Task LiftedBans_StopCountingAfterTheDaysSet_AndAStandingBanNeverDoes()
    {
        await using var host = await ReadyAsync();
        var now = host.Clock.UtcNow;

        // Banned and lifted 100 days ago.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_old", now.AddDays(-200)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_old", now.AddDays(-100)), Ct);

        // Banned long ago, lifted 10 days ago.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_recent", now.AddDays(-200)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_recent", now.AddDays(-10)), Ct);

        // Banned long ago and never lifted.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_standing", now.AddDays(-400)), Ct);

        // An old lifted ban, then banned again since: only the new one counts.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_again", now.AddDays(-300)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_again", now.AddDays(-250)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_again", now.AddDays(-5)), Ct);

        string[] people = ["usr_old", "usr_recent", "usr_standing", "usr_again"];

        // Unset, every ban counts, as before the setting existed.
        var always = await ReadAsync(host, null, people);
        Assert.All(people, p => Assert.True(always[p].IsFlagged));
        Assert.Equal("2 kicks or bans", always["usr_again"].Reason);

        await SetRulesAsync(host, new FlagRuleSettings { LiftedBansForDays = 90 });
        var windowed = await ReadAsync(host, null, people);

        Assert.False(windowed["usr_old"].IsFlagged);
        Assert.Equal(1, windowed["usr_old"].PriorActions);
        Assert.Equal("1 kick or ban", windowed["usr_recent"].Reason);
        Assert.Equal("1 kick or ban", windowed["usr_standing"].Reason);
        Assert.Equal("1 kick or ban", windowed["usr_again"].Reason);
        Assert.Equal(2, windowed["usr_again"].PriorActions);
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

        Assert.Null(sparse.LiftedBansForDays);
        Assert.Equal(FlagRuleSettings.MaxLiftedBanDays, FlagRuleSettings.Read("""{"liftedBansForDays":99999}""").LiftedBansForDays);
    }

    [Fact]
    public async Task TheCard_SavesTheLiftedBanDays_AndRefusesThemOutOfRange()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Null((await host.GetJsonAsync<FlagRulesView>(Path, cookie, Ct)).LiftedBansForDays);

        object Body(int? days) => new
        {
            kicksAndBans = true,
            liftedBansForDays = days,
            warns = true,
            warnsAtLeast = 5,
            nuisance = true,
            autoMod = true,
            everyAutoModRule = true,
            autoModRules = Array.Empty<Guid>(),
        };

        var saved = await host.PutJsonAsync(Path, Body(30), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(30, (await saved.Content.ReadFromJsonAsync<FlagRulesView>(Ct))!.LiftedBansForDays);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync(Path, Body(0), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync(Path, Body(3651), cookie, Ct)).StatusCode);

        var cleared = await host.PutJsonAsync(Path, Body(null), cookie, Ct);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<FlagRulesView>(Ct))!.LiftedBansForDays);
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
