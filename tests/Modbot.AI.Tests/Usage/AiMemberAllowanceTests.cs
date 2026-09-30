using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.AI.Tests.Insights;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.AI.Tests.Usage;

/// <summary>
/// The monthly AI allowance of each team member: a default for everyone and an amount of a member's
/// own, counted in tokens and in dollars from the same use the spend report reads, across every
/// feature, and stopping only calls made for that member.
/// </summary>
/// <remarks>The clock starts on Thursday 15 March 2029 at noon.</remarks>
[Collection(nameof(PostgresCollection))]
public class AiMemberAllowanceTests : InsightTestBase
{
    private const string Priced = "priced-model";
    private const string Unpriced = "local-model";

    public AiMemberAllowanceTests(PostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ATokenAllowance_StopsAMember_AtTheirOwnUseOfEveryFeatureTogether()
    {
        var sam = await UserAsync("sam");
        await DefaultAsync(tokens: 1_000);

        // 600 in Chat and 400 in insights: neither alone reaches 1,000.
        await SpendAsync(AiFeatures.Chat, 600, sam.Id);
        await using (var context = NewContext())
            Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Insights, sam.Id, Ct));

        await SpendAsync(AiFeatures.Insights, 400, sam.Id);

        await using (var context = NewContext())
        {
            var reached = await NewUsage(context).LimitReachedAsync(AiFeatures.Moderation, sam.Id, Ct);

            Assert.NotNull(reached);
            Assert.Equal(AiLimitReached.Allowance, reached.AppliesTo);
            Assert.Equal(AiLimitUnits.Tokens, reached.Unit);
            Assert.Equal(1_000m, reached.Limit);
            Assert.Equal(1_000m, reached.Spent);
            Assert.Equal("You have used your monthly AI allowance (1,000 tokens). It starts again on 1 April.", reached.Message);
        }
    }

    [Fact]
    public async Task AnAllowanceStopsOnlyCallsMadeForThatMember()
    {
        var sam = await UserAsync("sam");
        var alex = await UserAsync("alex");
        await DefaultAsync(tokens: 1_000);
        await SpendAsync(AiFeatures.Chat, 5_000, sam.Id);

        await using var context = NewContext();

        Assert.NotNull(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));

        // Another member has used nothing; a call with nobody behind it is nobody's to stop.
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, alex.Id, Ct));
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Insights, userId: null, Ct));
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Insights, Ct));
    }

    [Fact]
    public async Task UseWithNoAccountBehindIt_CountsAgainstNobodysAllowance()
    {
        var sam = await UserAsync("sam");
        await DefaultAsync(tokens: 1_000);

        // Scheduled insights, AutoMod's own checks and alert sentences carry no account.
        await SpendAsync(AiFeatures.Insights, 50_000, userId: null);
        await SpendAsync(AiFeatures.Moderation, 50_000, userId: null);

        await using var context = NewContext();
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));
    }

    [Fact]
    public async Task AnOwnAllowanceReplacesTheDefault_AndOneWithNoAmountsIsNoLimit()
    {
        var sam = await UserAsync("sam");
        var alex = await UserAsync("alex");
        await DefaultAsync(tokens: 1_000);
        await OwnAsync(sam.Id, tokens: 10_000);
        await OwnAsync(alex.Id, tokens: null);

        await SpendAsync(AiFeatures.Chat, 5_000, sam.Id);
        await SpendAsync(AiFeatures.Chat, 5_000, alex.Id);

        await using var context = NewContext();
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, alex.Id, Ct));

        await SpendAsync(AiFeatures.Chat, 5_000, sam.Id);

        await using var after = NewContext();
        Assert.Equal(10_000m, (await NewUsage(after).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct))?.Limit);
    }

    [Fact]
    public async Task AModelWithNoPrice_IsStoppedByTokens_ButNeverByMoney()
    {
        var sam = await UserAsync("sam");
        await DefaultAsync(tokens: null, money: 0.01m);
        await SpendAsync(AiFeatures.Chat, 5_000_000, sam.Id, model: Unpriced);

        // The cost of a model with no price is unknown, so it cannot reach a money allowance.
        await using (var context = NewContext())
            Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));

        await DefaultAsync(tokens: 1_000_000, money: 0.01m);

        await using (var context = NewContext())
            Assert.Equal(AiLimitUnits.Tokens, (await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct))?.Unit);
    }

    [Fact]
    public async Task AMoneyAllowance_StopsAtWhatAPricedModelCost()
    {
        var sam = await UserAsync("sam");
        await PriceAsync(Priced, 1m, 1m);
        await DefaultAsync(tokens: null, money: 1m);

        await SpendAsync(AiFeatures.Chat, 500_000, sam.Id);
        await using (var context = NewContext())
            Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));

        await SpendAsync(AiFeatures.Insights, 500_000, sam.Id);

        await using (var context = NewContext())
        {
            var reached = await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct);

            Assert.Equal(AiLimitUnits.Money, reached?.Unit);
            Assert.Equal("You have used your monthly AI allowance ($1.00). It starts again on 1 April.", reached?.Message);
        }
    }

    [Fact]
    public async Task AnAdministrator_AndAnAccountAllowedPastLimits_AreNotStopped_ButTheirUseIsStillCounted()
    {
        var admin = await UserAsync("admin", ModbotPermissions.Administrator);
        var past = await UserAsync("past", ModbotPermissions.UseAiPastLimits);
        var plain = await UserAsync("plain", ModbotPermissions.UseAiChat);
        await DefaultAsync(tokens: 1_000);

        foreach (var user in new[] { admin, past, plain })
            await SpendAsync(AiFeatures.Chat, 5_000, user.Id);

        await using var context = NewContext();
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, admin.Id, Ct));
        Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, past.Id, Ct));
        Assert.NotNull(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, plain.Id, Ct));

        var used = await new AiSpendReport(context, Clock).MonthByMemberAsync(Ct);
        Assert.Equal(5_000, used[admin.Id].Tokens);
    }

    [Fact]
    public async Task AllowancesStartAgainOnTheFirstOfTheNextMonth()
    {
        var sam = await UserAsync("sam");
        await DefaultAsync(tokens: 1_000);
        await SpendAsync(AiFeatures.Chat, 5_000, sam.Id);

        await using (var context = NewContext())
            Assert.NotNull(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));

        Clock.UtcNow = new DateTimeOffset(2029, 4, 1, 0, 0, 1, TimeSpan.Zero);

        await using (var context = NewContext())
            Assert.Null(await NewUsage(context).LimitReachedAsync(AiFeatures.Chat, sam.Id, Ct));
    }

    [Fact]
    public async Task AMemberWhoHasUsedTheirAllowance_IsToldSo_AndTheAttemptIsInTheCallLogAsTheirs()
    {
        await TurnAiOnAsync();
        var sam = await UserAsync("sam");
        await DefaultAsync(tokens: 1_000);
        await SpendAsync(AiFeatures.Chat, 5_000, sam.Id);

        await using (var context = NewContext())
        {
            var attempt = await NewWriter(context).WriteAsync(
                InsightKinds.Group, InsightKinds.EveryWeek, new DateOnly(2029, 3, 15), InsightStart.Button(sam.Id, "sam"), Ct);

            Assert.Equal("You have used your monthly AI allowance (1,000 tokens). It starts again on 1 April.", attempt.NotAsked);
            Assert.Empty(Model.Requests);
        }

        await using var read = NewContext();
        var row = await read.AiCalls.AsNoTracking().SingleAsync(c => c.Feature == AiFeatures.Insights, Ct);
        Assert.Equal(AiCallOutcomes.Limited, row.Outcome);
        Assert.Equal(sam.Id, row.UserId);

        // The same insight on its schedule has nobody behind it and goes ahead.
        await using var scheduled = NewContext();
        var next = await NewWriter(scheduled).WriteAsync(
            InsightKinds.Group, InsightKinds.EveryWeek, new DateOnly(2029, 3, 15), InsightStart.Schedule(null), Ct);
        Assert.Null(next.NotAsked);
    }

    [Fact]
    public async Task TheLimitForEveryoneIsStillTheCeilingForAMemberPastTheirLimits()
    {
        var admin = await UserAsync("admin", ModbotPermissions.Administrator);
        await PriceAsync(Priced, 1m, 1m);
        await SpendAsync(AiFeatures.Chat, 2_000_000, admin.Id);

        await using (var context = NewContext())
        {
            context.AiSpendLimits.Add(new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerMonth = 1m, UpdatedAt = Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        await using var check = NewContext();
        Assert.Equal("This Modbot's monthly AI spend limit is reached.",
            (await NewUsage(check).LimitReachedAsync(AiFeatures.Chat, admin.Id, Ct))?.Message);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private async Task DefaultAsync(long? tokens, decimal? money = null)
    {
        await using var context = NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.AiMemberMonthlyTokens = tokens;
        settings.AiMemberMonthlyMoney = money;
        await context.SaveChangesAsync(Ct);
    }

    private async Task OwnAsync(Guid userId, long? tokens, decimal? money = null)
    {
        await using var context = NewContext();
        context.AiMemberAllowances.Add(new AiMemberAllowance
        {
            UserId = userId,
            MonthlyTokens = tokens,
            MonthlyMoney = money,
            UpdatedAt = Clock.UtcNow,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task PriceAsync(string model, decimal input, decimal output)
    {
        await using var context = NewContext();
        context.AiModelPrices.Add(new AiModelPrice { Model = model, InputPerMillion = input, OutputPerMillion = output, UpdatedAt = Clock.UtcNow });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SpendAsync(string feature, int input, Guid? userId, string model = Priced)
    {
        await using var context = NewContext();
        context.AiUsage.Add(new AiUsage
        {
            At = Clock.UtcNow,
            Feature = feature,
            UserId = userId,
            Model = model,
            InputTokens = input,
            OutputTokens = 0,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<ModbotUser> UserAsync(string name, ModbotPermissions permissions = ModbotPermissions.None)
    {
        await using var context = NewContext();

        var user = new ModbotUser { Username = name, UsernameNormalized = name.ToUpperInvariant(), PasswordHash = "x" };
        context.Users.Add(user);

        var role = new ModbotRole
        {
            Name = $"{name} role",
            NameNormalized = $"{name} role".ToUpperInvariant(),
            Permissions = permissions,
            CreatedAt = Clock.UtcNow,
        };
        context.Roles.Add(role);
        context.UserRoles.Add(new ModbotUserRole { UserId = user.Id, RoleId = role.Id });

        await context.SaveChangesAsync(Ct);
        return user;
    }
}
