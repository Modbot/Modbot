using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.ProblemAvatars;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.ProblemAvatars;

/// <summary>
/// The problem avatar list's endpoints: kept by whoever may change settings, add and remove,
/// and marking the avatar a person was last seen wearing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ProblemAvatarEndpointsTests
{
    private const string Url = "/api/problem-avatars";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public ProblemAvatarEndpointsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ReadSurfaceTestHost Host, string Cookie)> StartAsync()
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        return (host, await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct));
    }

    private static Task WearAsync(ReadSurfaceTestHost host, string person, string type, string? avatar, DateTimeOffset at)
        => host.WriteFactAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = person,
                WorldId = "wrld_4b34",
                InstanceId = "39911",
                Source = FactSource.Modbot,
                Data = avatar is null ? null : new JsonObject { ["avatarName"] = avatar },
            },
            Ct);

    /// <summary>Puts a name on the list the way a moderator does: by marking a person seen wearing it.</summary>
    private static async Task<HttpResponseMessage> MarkWornAsync(
        ReadSurfaceTestHost host, string cookie, string person, string avatar)
    {
        await WearAsync(host, person, FactType.AvatarChanged, avatar, Noon);

        return await host.PostJsonAsync($"{Url}/from-person", new { vrchatUserId = person }, cookie, Ct);
    }

    private static Task<HttpResponseMessage> DeleteAsync(ReadSurfaceTestHost host, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("Cookie", cookie);

        return host.Client.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    [Fact]
    public async Task NothingMarked_IsAnEmptyList()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        Assert.Empty(await host.GetJsonAsync<List<ProblemAvatarView>>(Url, cookie, Ct));
    }

    [Fact]
    public async Task MarkingAPersonsAvatar_AddsItsExactName_FromTheNewestSighting()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        await WearAsync(host, "usr_wearer", FactType.AvatarChanged, "Plain Fox", Noon.AddMinutes(-30));
        await WearAsync(host, "usr_wearer", FactType.AvatarChanged, "White  Tiger (v2)", Noon.AddMinutes(-10));

        // A later join names no avatar, so it is looked past.
        await WearAsync(host, "usr_wearer", FactType.InstanceJoined, null, Noon);

        var added = await ReadAsync<ProblemAvatarView>(
            await host.PostJsonAsync($"{Url}/from-person", new { vrchatUserId = "usr_wearer" }, cookie, Ct));

        Assert.Equal("White  Tiger (v2)", added.Name);

        var list = await host.GetJsonAsync<List<ProblemAvatarView>>(Url, cookie, Ct);
        Assert.Equal("White  Tiger (v2)", Assert.Single(list).Name);
    }

    [Fact]
    public async Task MarkingSomeoneNeverSeenInAnAvatar_IsNotFound()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        await WearAsync(host, "usr_bare", FactType.InstanceJoined, null, Noon);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.PostJsonAsync($"{Url}/from-person", new { vrchatUserId = "usr_bare" }, cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.PostJsonAsync($"{Url}/from-person", new { vrchatUserId = " " }, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheSameAvatarTwice_IsOneEntry_WhateverItsCase()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var first = await ReadAsync<ProblemAvatarView>(await MarkWornAsync(host, cookie, "usr_one", "White Tiger"));
        var again = await ReadAsync<ProblemAvatarView>(await MarkWornAsync(host, cookie, "usr_two", " white tiger"));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("White Tiger", again.Name);
        Assert.Single(await host.GetJsonAsync<List<ProblemAvatarView>>(Url, cookie, Ct));
    }

    [Fact]
    public async Task ANameLongerThanTheColumn_IsCutToFit_NotAnError_AndStillMatchesItself()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var tooLong = new string('a', ProblemAvatar.MaxNameLength + 50);

        var first = await ReadAsync<ProblemAvatarView>(await MarkWornAsync(host, cookie, "usr_one", tooLong));
        var again = await ReadAsync<ProblemAvatarView>(await MarkWornAsync(host, cookie, "usr_two", tooLong));

        Assert.Equal(ProblemAvatar.MaxNameLength, first.Name.Length);
        Assert.Equal(first.Id, again.Id);
        Assert.True((await host.GetJsonAsync<PersonAvatarView>($"{Url}/person?vrchat=usr_one", cookie, Ct)).Listed);
    }

    [Fact]
    public async Task RemovingAnEntry_TakesItOffTheList_AndASecondRemoveIsNotFound()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var added = await ReadAsync<ProblemAvatarView>(await MarkWornAsync(host, cookie, "usr_wearer", "White Tiger"));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(host, $"{Url}/{added.Id}", cookie)).StatusCode);
        Assert.Empty(await host.GetJsonAsync<List<ProblemAvatarView>>(Url, cookie, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(host, $"{Url}/{added.Id}", cookie)).StatusCode);
    }

    [Fact]
    public async Task AddingAndRemoving_AreRecordedAsSettingsChanges()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var added = await ReadAsync<ProblemAvatarView>(await MarkWornAsync(host, cookie, "usr_wearer", "White Tiger"));
        await DeleteAsync(host, $"{Url}/{added.Id}", cookie);

        await using var context = _db.NewContext();
        var changes = await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.SettingsChanged && e.Data.Contains("problemAvatars"))
            .CountAsync(Ct);

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task WhatAPersonWears_ComesBackWithWhetherItIsListed()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        await WearAsync(host, "usr_wearer", FactType.AvatarChanged, "White Tiger", Noon);

        var before = await host.GetJsonAsync<PersonAvatarView>($"{Url}/person?vrchat=usr_wearer", cookie, Ct);
        Assert.Equal("White Tiger", before.AvatarName);
        Assert.False(before.Listed);

        // Someone else wearing the same name in other letters marks it; the first person's card then reads listed.
        await MarkWornAsync(host, cookie, "usr_other", "white tiger");

        Assert.True((await host.GetJsonAsync<PersonAvatarView>($"{Url}/person?vrchat=usr_wearer", cookie, Ct)).Listed);

        var none = await host.GetJsonAsync<PersonAvatarView>($"{Url}/person?vrchat=usr_nobody", cookie, Ct);
        Assert.Null(none.AvatarName);
        Assert.False(none.Listed);
    }

    [Fact]
    public async Task WithoutTheSettingsPermission_EveryRouteIsForbidden()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"{Url}/person?vrchat=usr_wearer", cookie, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PostJsonAsync($"{Url}/from-person", new { vrchatUserId = "usr_wearer" }, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(host, $"{Url}/{id}", cookie)).StatusCode);

        await using var context = _db.NewContext();
        Assert.Empty(await context.ProblemAvatars.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task NotSignedIn_IsUnauthorised()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(Url, Ct)).StatusCode);
    }
}
