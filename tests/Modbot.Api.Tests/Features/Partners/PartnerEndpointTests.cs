using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Partners;

/// <summary>
/// The partner tracker API: seeing and changing both need Manage partners, the list runs from the
/// most recent collab, and each change is a fact that names the fields but never the contacts.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PartnerEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
        {
            await context.Partners.ExecuteDeleteAsync(Ct);
            await context.OtherGroupNames.ExecuteDeleteAsync(Ct);
        }

        return await ApiTestHost.StartAsync(db);
    }

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, object body)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/partners", body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    [Fact]
    public async Task SeeingAndChangingNeedManagePartners()
    {
        await using var host = await StartAsync();
        var (_, other) = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/partners", null, other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, "/api/partners", new { name = "Moon Club" }, other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/partners/{Guid.NewGuid()}", new { name = "Moon Club" }, other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/partners/{Guid.NewGuid()}", null, other, Ct)).StatusCode);

        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendJsonAsync(HttpMethod.Get, "/api/partners", null, manager, Ct)).StatusCode);
    }

    [Fact]
    public async Task APartnerIsKept_AndTheListRunsFromTheMostRecentCollab()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);

        await CreateAsync(host, manager, new { name = "Zebra Club" });
        await CreateAsync(host, manager, new { name = "Apple Club" });
        await CreateAsync(host, manager, new { name = "Old Friends", lastCollabOn = "2026-03-01", lastCollabNote = "Movie night" });
        var newest = await CreateAsync(host, manager, new
        {
            name = "  New Friends ",
            groupId = "grp_anything-at-all",
            status = "paused",
            envoy = "Mina",
            contacts = new[] { " Sam (Discord: sam) ", "", "Pat" },
            lastCollabOn = "2026-09-20",
            lastCollabNote = "Karaoke",
            notes = "Asked for a fall event",
        });

        var listed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/partners", null, manager, Ct), Ct);
        var names = listed.GetProperty("partners").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["New Friends", "Old Friends", "Apple Club", "Zebra Club"], names);

        var first = listed.GetProperty("partners")[0];
        Assert.Equal(newest, first.GetProperty("id").GetGuid());
        Assert.Equal("paused", first.GetProperty("status").GetString());
        Assert.Equal("Mina", first.GetProperty("envoy").GetString());
        Assert.Equal("2026-09-20", first.GetProperty("lastCollabOn").GetString());
        Assert.Equal(["Sam (Discord: sam)", "Pat"], first.GetProperty("contacts").EnumerateArray().Select(c => c.GetString()).ToList());
        Assert.Single(await host.FactsAsync(FactType.PartnerCreated, newest.ToString(), Ct));
    }

    [Fact]
    public async Task AnEmptyNameIsReadFromAGroupNameModbotAlreadyHolds()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);

        await using (var context = db.NewContext())
        {
            context.OtherGroupNames.Add(new OtherGroupName { GroupId = "grp_known", Name = "Known Group", AskedAt = host.Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        var id = await CreateAsync(host, manager, new { groupId = "grp_known" });

        await using var read = db.NewContext();
        Assert.Equal("Known Group", (await read.Partners.SingleAsync(p => p.Id == id, Ct)).Name);

        var nameless = await host.SendJsonAsync(HttpMethod.Post, "/api/partners", new { groupId = "grp_unknown" }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
        Assert.Equal("Give the group a name.", await ErrorOf(nameless));
    }

    [Fact]
    public async Task AContactOnMoreThanOneLineIsRefused()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/partners", new { name = "Moon Club", contacts = new[] { "Sam\nPat" } }, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A contact must be on one line.", await ErrorOf(response));
    }

    [Fact]
    public async Task ABadStatusIsRefused()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/partners", new { name = "Moon Club", status = "finished" }, manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The status must be active, paused or ended.", await ErrorOf(response));
    }

    [Fact]
    public async Task ChangingAPartnerNamesTheFieldsInTheFact_NeverTheContacts()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);
        var id = await CreateAsync(host, manager, new { name = "Moon Club", contacts = new[] { "Sam" } });

        var same = await host.SendJsonAsync(HttpMethod.Put, $"/api/partners/{id}",
            new { name = "Moon Club", contacts = new[] { "Sam" } }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Empty(await host.FactsAsync(FactType.PartnerChanged, id.ToString(), Ct));

        var changed = await host.SendJsonAsync(HttpMethod.Put, $"/api/partners/{id}",
            new { name = "Moon Club", envoy = "Mina", contacts = new[] { "Secret Sam" } }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var fact = Assert.Single(await host.FactsAsync(FactType.PartnerChanged, id.ToString(), Ct));
        var data = fact.Data;
        Assert.Contains("envoy", data);
        Assert.Contains("contacts", data);
        Assert.DoesNotContain("Secret Sam", data);

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Put, $"/api/partners/{Guid.NewGuid()}", new { name = "x" }, manager, Ct)).StatusCode);
    }

    [Fact]
    public async Task DeletingAPartnerRemovesItAndIsAFact()
    {
        await using var host = await StartAsync();
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManagePartners, Ct);
        var id = await CreateAsync(host, manager, new { name = "Moon Club" });

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"/api/partners/{id}", null, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var read = db.NewContext();
        Assert.False(await read.Partners.AnyAsync(p => p.Id == id, Ct));
        Assert.Single(await host.FactsAsync(FactType.PartnerDeleted, id.ToString(), Ct));

        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendJsonAsync(HttpMethod.Delete, $"/api/partners/{id}", null, manager, Ct)).StatusCode);
    }
}
