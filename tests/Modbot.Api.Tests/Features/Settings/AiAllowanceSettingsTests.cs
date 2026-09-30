using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → AI → Limits → Monthly AI allowance per team member: who may set it, what is stored,
/// what the screen reads back, and the audit entry a change writes.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AiAllowanceSettingsTests
{
    private const string Path = "/api/settings/ai/allowances";
    private const string LimitsPath = "/api/settings/ai/limits";

    private readonly PostgresFixture _db;

    public AiAllowanceSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SettingIt_NeedsManageSettings()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path, Body(1_000, null), cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ADefaultAndAnOwnAmountAreSaved_AndReadBackWithEachMembersUseThisMonth()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        await using (var db = _db.NewContext())
        {
            db.AiUsage.Add(new AiUsage
            {
                At = host.Clock.UtcNow,
                Feature = "chat",
                UserId = other.Id,
                Model = "local-model",
                InputTokens = 700,
                OutputTokens = 300,
            });
            await db.SaveChangesAsync(Ct);
        }

        var saved = await host.SendJsonAsync(HttpMethod.Put, Path, Body(
            defaultTokens: 5_000, defaultMoney: 2.5m, (other.Id, 8_000, null)), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var read = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, LimitsPath, null, cookie, Ct), Ct))
            .GetProperty("allowances");

        Assert.Equal(5_000, read.GetProperty("default").GetProperty("tokens").GetInt64());
        Assert.Equal(2.5m, read.GetProperty("default").GetProperty("money").GetDecimal());

        var members = read.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("userId").GetGuid());

        // The member with an amount of their own: theirs replaces the default whole, dollars included.
        var theirs = members[other.Id];
        Assert.Equal(8_000, theirs.GetProperty("allowance").GetProperty("tokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("allowance").GetProperty("money").ValueKind);
        Assert.Equal(1_000, theirs.GetProperty("month").GetProperty("inputTokens").GetInt64() + theirs.GetProperty("month").GetProperty("outputTokens").GetInt64());
        Assert.False(theirs.GetProperty("pastLimits").GetBoolean());

        // The one who did not get an amount uses the default.
        var mine = members[admin.Id];
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("own").ValueKind);
        Assert.Equal(5_000, mine.GetProperty("allowance").GetProperty("tokens").GetInt64());
    }

    [Fact]
    public async Task AChangeIsInTheAuditLog_NamingWhatItWasAndWhatItBecame_AndAnUnchangedSaveIsNot()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var first = await host.SendJsonAsync(HttpMethod.Put, Path, Body(1_000, null, (other.Id, 200, null)), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var facts = (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))
            .Select(ApiTestHost.DataOf)
            .Where(d => d.GetProperty("setting").GetString() == "aiAllowances")
            .ToList();

        var written = Assert.Single(facts);
        Assert.Equal(JsonValueKind.Null, written.GetProperty("before").GetProperty("default").GetProperty("tokens").ValueKind);
        Assert.Equal(1_000, written.GetProperty("after").GetProperty("default").GetProperty("tokens").GetInt64());

        var member = Assert.Single(written.GetProperty("after").GetProperty("members").EnumerateArray());
        Assert.Equal(other.Username, member.GetProperty("username").GetString());
        Assert.Equal(200, member.GetProperty("tokens").GetInt64());

        // Saved again as it is: nothing changed, so nothing is recorded.
        await host.SendJsonAsync(HttpMethod.Put, Path, Body(1_000, null, (other.Id, 200, null)), cookie, Ct);
        Assert.Single(
            await host.FactsAsync(FactType.SettingsChanged, "settings", Ct),
            f => ApiTestHost.DataOf(f).GetProperty("setting").GetString() == "aiAllowances");

        // Taking the member's own amount away goes back to the default, and is recorded.
        await host.SendJsonAsync(HttpMethod.Put, Path, new { members = Array.Empty<object>() }, cookie, Ct);

        var all = (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))
            .Select(ApiTestHost.DataOf)
            .Where(d => d.GetProperty("setting").GetString() == "aiAllowances")
            .ToList();
        Assert.Equal(2, all.Count);

        await using var db = _db.NewContext();
        Assert.Empty(await db.AiMemberAllowances.ToListAsync(Ct));
        Assert.Equal(1_000, (await db.GetSettingsAsync(Ct)).AiMemberMonthlyTokens);

        // Each entry names the account that made the change.
        var recorded = (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))
            .Where(f => ApiTestHost.DataOf(f).GetProperty("setting").GetString() == "aiAllowances")
            .ToList();
        Assert.Equal(2, recorded.Count);
        Assert.All(recorded, f => Assert.Equal(admin.Id.ToString(), f.ActorId));
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -0.5)]
    public async Task ANegativeAmountIsRefused(long? tokens, double? money)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path,
            new { @default = new { tokens, money = money is null ? (decimal?)null : (decimal)money } }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnAllowanceForNobodyOrForTheSameMemberTwiceIsRefused()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var nobody = await host.SendJsonAsync(HttpMethod.Put, Path, Body(null, null, (Guid.NewGuid(), 10, null)), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nobody.StatusCode);

        var twice = await host.SendJsonAsync(HttpMethod.Put, Path, Body(null, null, (admin.Id, 10, null), (admin.Id, 20, null)), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static object Body(long? defaultTokens, decimal? defaultMoney, params (Guid UserId, long? Tokens, decimal? Money)[] members) => new
    {
        @default = new { tokens = defaultTokens, money = defaultMoney },
        members = members.Select(m => new { userId = m.UserId, tokens = m.Tokens, money = m.Money }).ToList(),
    };
}
