using Modbot.Core.Giveaways;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Giveaways;

/// <summary>
/// <c>CheckVRChatUserAsync</c>: the same rule tree asked about one VRChat account, which is what
/// auto-invites call (auto-invites design §3.2), and the two rules that feature added.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class OnePersonRuleCheckTests(PostgresFixture fixture) : GiveawayTestBase(fixture)
{
    private const string Alice = "usr_alice";
    private const string AliceDiscord = "1000000000000001";

    private static GiveawayRule Rule(string kind, decimal? amount = null, string? id = null)
        => new() { Kind = kind, Amount = amount, Id = id };

    private async Task<bool> PassesAsync(GiveawayRule rule, string userId = Alice)
    {
        await using var context = Database.NewContext();
        var answer = await NewChecker(context).CheckVRChatUserAsync(rule, userId, Ct);
        return answer.Met;
    }

    // ── Trust rank ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrustRankPassesAtTheRankAsked()
    {
        await AddPersonAsync(vrchat: Alice, trustRank: TrustRank.TrustedUser);

        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "TrustedUser")));
    }

    [Fact]
    public async Task TrustRankPassesAboveTheRankAsked()
    {
        await AddPersonAsync(vrchat: Alice, trustRank: TrustRank.Legend);

        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "TrustedUser")));
    }

    [Fact]
    public async Task TrustRankFailsBelowTheRankAsked()
    {
        await AddPersonAsync(vrchat: Alice, trustRank: TrustRank.KnownUser);

        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "TrustedUser")));
    }

    [Fact]
    public async Task ANuisanceIsNeverTrustedOrBetter()
    {
        await AddPersonAsync(vrchat: Alice, trustRank: TrustRank.Nuisance);

        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "TrustedUser")));
        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "Visitor")));
    }

    [Fact]
    public async Task ARankNobodyHasReadFails()
    {
        await AddPersonAsync(vrchat: Alice);

        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "Visitor")));
    }

    // ── 18+ ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The18PlusRulePassesForSomebodyModbotHasSeenVerified()
    {
        await AddPersonAsync(vrchat: Alice, verified18Plus: true);

        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.Age18Plus)));
    }

    [Fact]
    public async Task The18PlusRuleFailsForEverybodyElse()
    {
        await AddPersonAsync(vrchat: Alice);

        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.Age18Plus)));
    }

    // ── The rules that were already there, asked about one person ────────────────────────

    [Fact]
    public async Task AccountAgePassesAndFailsAgainstTheStoredJoinDate()
    {
        await AddPersonAsync(vrchat: Alice, accountDays: 100);

        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.VRChatAccountDays, 30)));
        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.VRChatAccountDays, 365)));
    }

    [Fact]
    public async Task AccountAgeFailsForSomebodyWhoseProfileHasNeverBeenRead()
    {
        await AddPersonAsync(vrchat: Alice);

        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.VRChatAccountDays, 1)));
    }

    [Fact]
    public async Task NoTroubleFailsForSomebodyWithATroubleFact()
    {
        await AddPersonAsync(vrchat: Alice, accountDays: 100);

        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.NoTrouble)));

        await TroubleAsync(Alice, Modbot.Core.Data.Entities.FactPlatform.VRChat);

        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.NoTrouble)));
    }

    [Fact]
    public async Task TheDiscordSideIsReadThroughTheLink()
    {
        await AddPersonAsync(vrchat: Alice, discord: AliceDiscord, inDiscordDays: 90, link: true);

        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.DiscordMemberDays, 30)));
        Assert.True(await PassesAsync(Rule(GiveawayRuleKinds.LinkedAccounts)));
    }

    [Fact]
    public async Task SomebodyModbotHasNeverHeardOfFailsEveryRuleAboutThem()
    {
        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.VRChatAccountDays, 1), "usr_nobody"));
        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.Age18Plus), "usr_nobody"));
        Assert.False(await PassesAsync(Rule(GiveawayRuleKinds.InGroup), "usr_nobody"));
    }

    [Fact]
    public async Task AnEmptyTreeLetsAnybodyThrough()
        => Assert.True(await PassesAsync(GiveawayRule.Everyone, "usr_nobody"));

    [Fact]
    public async Task AllOfNeedsEveryRuleInside()
    {
        await AddPersonAsync(vrchat: Alice, accountDays: 100, trustRank: TrustRank.TrustedUser);

        var rule = new GiveawayRule
        {
            Kind = GiveawayRuleKinds.AllOf,
            Rules =
            [
                Rule(GiveawayRuleKinds.VRChatAccountDays, 30),
                Rule(GiveawayRuleKinds.TrustRankAtLeast, id: "TrustedUser"),
            ],
        };

        Assert.True(await PassesAsync(rule));

        var stricter = rule with
        {
            Rules = [.. rule.Rules, Rule(GiveawayRuleKinds.Age18Plus)],
        };

        Assert.False(await PassesAsync(stricter));
    }
}
