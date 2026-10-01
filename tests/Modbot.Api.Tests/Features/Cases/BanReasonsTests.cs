using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Cases;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Cases;

/// <summary>
/// The reason list: seeded with plain defaults, read by anyone signed in, changed only by
/// <c>EditClassifications</c>, never deleted, and every change a fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BanReasonsTests
{
    private readonly PostgresFixture _db;

    public BanReasonsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task AnUnauthenticatedCaller_Gets401_OnEveryEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/settings/ban-reasons", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsync("/api/settings/ban-reasons", System.Net.Http.Json.JsonContent.Create(new { label = "x" }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsync($"/api/settings/ban-reasons/{Guid.NewGuid()}", System.Net.Http.Json.JsonContent.Create(new { label = "x" }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsync("/api/settings/ban-reasons/order", System.Net.Http.Json.JsonContent.Create(new { ids = new[] { Guid.NewGuid() } }), ct)).StatusCode);
    }

    [Fact]
    public async Task TheDefaultsAreSeededOnFirstRead_AndOtherNeedsAWrittenReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var list = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);

        Assert.False(list.CanEdit);
        Assert.Equal(BanReasonList.Defaults.Count, list.Reasons.Count);
        Assert.Equal(BanReasonList.Defaults.Select(d => d.Label), list.Reasons.Select(r => r.Label));
        Assert.All(list.Reasons, r => Assert.True(r.IsActive));

        // One "Other" for the ban list and one for unbans, and each needs the words.
        var others = list.Reasons.Where(r => r.Label == "Other").ToList();
        Assert.Equal(2, others.Count);
        Assert.All(others, o => Assert.True(o.NeedsWrittenReason));
        Assert.All(list.Reasons.Where(r => r.Label != "Other"), r => Assert.False(r.NeedsWrittenReason));

        // The ban reasons serve bans, kicks and rejections; the unban reasons serve unbans only.
        Assert.Equal(["ban", "kick", "reject"], list.Reasons.Single(r => r.Label == "Harassment").UsedFor);
        Assert.Equal(["unban"], list.Reasons.Single(r => r.Label == "Appeal upheld").UsedFor);
        Assert.Equal(
            ["Mistake", "Appeal upheld", "Time served", "Other"],
            list.Reasons.Where(r => r.UsedFor.Contains("unban")).Select(r => r.Label));
        Assert.DoesNotContain(list.Reasons, r => r.UsedFor.Contains("ban") && r.UsedFor.Contains("unban"));

        // Off until a group asks for it.
        Assert.False(list.ReasonAlwaysRequired);

        // Reading again does not seed again.
        var again = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);
        Assert.Equal(list.Reasons.Select(r => r.Id), again.Reasons.Select(r => r.Id));
    }

    [Fact]
    public async Task WithoutEditClassifications_ChangesAre403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile | ModbotPermissions.ManageSettings, ct);
        var list = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/settings/ban-reasons", new { label = "Doxxing" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync($"/api/settings/ban-reasons/{list.Reasons[0].Id}", new { label = "Renamed" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync("/api/settings/ban-reasons/order", new { ids = list.Reasons.Select(r => r.Id).Reverse() }, cookie, ct)).StatusCode);
    }

    [Fact]
    public async Task AddRewordSwitchOffAndReorder_EachIsAFact_AndNothingIsDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditClassifications, ct);
        var seeded = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);
        Assert.True(seeded.CanEdit);

        // Add: lands at the end.
        var added = await host.PostJsonAsync("/api/settings/ban-reasons", new { label = "Doxxing", description = "Sharing somebody's private details.", needsWrittenReason = true }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var doxxing = Deserialize<BanReasonView>(await added.Content.ReadAsStringAsync(ct));
        Assert.Equal(seeded.Reasons.Count, doxxing.SortOrder);
        Assert.True(doxxing.NeedsWrittenReason);

        // A blank label is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync("/api/settings/ban-reasons", new { label = "  " }, cookie, ct)).StatusCode);

        // Reword and switch off.
        var spam = seeded.Reasons.Single(r => r.Label == "Spam");
        var changed = await host.PutJsonAsync($"/api/settings/ban-reasons/{spam.Id}", new { label = "Spam or advertising", description = spam.Description, needsWrittenReason = false, isActive = false }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var off = Deserialize<BanReasonView>(await changed.Content.ReadAsStringAsync(ct));
        Assert.Equal("Spam or advertising", off.Label);
        Assert.False(off.IsActive);

        // Reorder: the new one first, and the switched-off one still listed.
        var ids = new List<Guid> { doxxing.Id };
        ids.AddRange(seeded.Reasons.Where(r => r.Id != spam.Id).Select(r => r.Id));
        var reordered = await host.PutJsonAsync("/api/settings/ban-reasons/order", new { ids }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);

        var after = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);
        Assert.Equal(seeded.Reasons.Count + 1, after.Reasons.Count);
        Assert.Equal(doxxing.Id, after.Reasons[0].Id);
        Assert.Equal(spam.Id, after.Reasons[^1].Id);
        Assert.False(after.Reasons[^1].IsActive);

        // Unknown id: 404 on change, 400 on reorder.
        Assert.Equal(HttpStatusCode.NotFound, (await host.PutJsonAsync($"/api/settings/ban-reasons/{Guid.NewGuid()}", new { label = "x" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PutJsonAsync("/api/settings/ban-reasons/order", new { ids = new[] { Guid.NewGuid() } }, cookie, ct)).StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var facts = await db.Events.AsNoTracking().Where(e => e.Type == FactType.BanReasonsChanged).OrderBy(e => e.Id).ToListAsync(ct);

        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactPlatform.Modbot, f.ActorPlatform));
        Assert.Contains("\"create\"", facts[0].Data);
        Assert.Contains("Doxxing", facts[0].Data);
        Assert.Contains("\"change\"", facts[1].Data);
        Assert.Contains("\"before\"", facts[1].Data);
        Assert.Contains("\"reorder\"", facts[2].Data);
    }

    [Fact]
    public async Task WhichActionsOfferAReason_CanBeSetAndChanged_AndIsInTheFact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditClassifications, ct);
        var seeded = await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct);

        // Left out on create: what a ban reason always served.
        var plain = Deserialize<BanReasonView>(await (await host.PostJsonAsync(
            "/api/settings/ban-reasons", new { label = "Doxxing" }, cookie, ct)).Content.ReadAsStringAsync(ct));
        Assert.Equal(["ban", "kick", "reject"], plain.UsedFor);

        // Given: exactly that, in the list's own order whatever order it was sent in.
        var unbanOnly = Deserialize<BanReasonView>(await (await host.PostJsonAsync(
            "/api/settings/ban-reasons", new { label = "Wrong person", usedFor = new[] { "unban" } }, cookie, ct)).Content.ReadAsStringAsync(ct));
        Assert.Equal(["unban"], unbanOnly.UsedFor);

        var both = Deserialize<BanReasonView>(await (await host.PostJsonAsync(
            "/api/settings/ban-reasons", new { label = "Raid", usedFor = new[] { "reject", "ban" } }, cookie, ct)).Content.ReadAsStringAsync(ct));
        Assert.Equal(["ban", "reject"], both.UsedFor);

        // Nothing at all, or an action that is not one of the four, is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync(
            "/api/settings/ban-reasons", new { label = "Nothing", usedFor = Array.Empty<string>() }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync(
            "/api/settings/ban-reasons", new { label = "Warned", usedFor = new[] { "warn" } }, cookie, ct)).StatusCode);

        // An update that leaves it out keeps it; one that sends it changes it.
        var harassment = seeded.Reasons.Single(r => r.Label == "Harassment");
        var kept = Deserialize<BanReasonView>(await (await host.PutJsonAsync(
            $"/api/settings/ban-reasons/{harassment.Id}",
            new { label = "Harassment", description = harassment.Description }, cookie, ct)).Content.ReadAsStringAsync(ct));
        Assert.Equal(harassment.UsedFor, kept.UsedFor);

        var changed = Deserialize<BanReasonView>(await (await host.PutJsonAsync(
            $"/api/settings/ban-reasons/{harassment.Id}",
            new { label = "Harassment", description = harassment.Description, usedFor = new[] { "ban" } }, cookie, ct)).Content.ReadAsStringAsync(ct));
        Assert.Equal(["ban"], changed.UsedFor);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var last = await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.BanReasonsChanged)
            .OrderByDescending(e => e.Id)
            .FirstAsync(ct);

        var data = System.Text.Json.JsonDocument.Parse(last.Data!).RootElement;
        Assert.Equal(3, data.GetProperty("before").GetProperty("usedFor").GetArrayLength());
        Assert.Equal("ban", data.GetProperty("after").GetProperty("usedFor")[0].GetString());
    }

    [Fact]
    public async Task RequiringAReasonEverywhere_IsASwitch_OnlyEditClassificationsCanFlip_AndItIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsync(
            "/api/settings/ban-reasons/required", System.Net.Http.Json.JsonContent.Create(new { required = true }), ct)).StatusCode);

        var reader = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ManageSettings, ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(
            "/api/settings/ban-reasons/required", new { required = true }, reader, ct)).StatusCode);

        var editor = await host.SignedInAsync(ModbotPermissions.EditClassifications, ct);
        Assert.Equal(HttpStatusCode.OK, (await host.PutJsonAsync(
            "/api/settings/ban-reasons/required", new { required = true }, editor, ct)).StatusCode);

        // Everybody reading the list learns it, so a dialog can wait for a reason before sending.
        Assert.True((await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", reader, ct)).ReasonAlwaysRequired);

        // Saying it again changes nothing and records nothing.
        Assert.Equal(HttpStatusCode.OK, (await host.PutJsonAsync(
            "/api/settings/ban-reasons/required", new { required = true }, editor, ct)).StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        Assert.True((await db.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).RequireModerationClassification);

        var fact = Assert.Single(await db.Events.AsNoTracking().Where(e => e.Type == FactType.SettingsChanged).ToListAsync(ct));
        var data = System.Text.Json.JsonDocument.Parse(fact.Data!).RootElement;
        Assert.Equal("banReasons", data.GetProperty("setting").GetString());
        Assert.True(data.GetProperty("changed").GetProperty("reasonAlwaysRequired").GetProperty("new").GetBoolean());
    }

    private static T Deserialize<T>(string json)
        => System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
}
