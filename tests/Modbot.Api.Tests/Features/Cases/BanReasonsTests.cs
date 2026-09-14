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

        var other = Assert.Single(list.Reasons, r => r.Label == "Other");
        Assert.True(other.NeedsWrittenReason);
        Assert.All(list.Reasons.Where(r => r.Label != "Other"), r => Assert.False(r.NeedsWrittenReason));

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

    private static T Deserialize<T>(string json)
        => System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
}
