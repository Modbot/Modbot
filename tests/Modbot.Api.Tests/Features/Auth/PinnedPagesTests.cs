using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Auth.Account;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Auth;

/// <summary>
/// The pages a person pins in the menu are kept on their account, so they follow them from a
/// phone to a desk, and the app is told them with the rest of who is signed in.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PinnedPagesTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APersonWhoHasNotPinnedAnythingHasNoListYet()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var me = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, cookie, Ct);

        // Null, not empty: the app shows its default pins until a list has been saved.
        Assert.Equal(JsonValueKind.Null, (await ApiTestHost.BodyOf(me, Ct)).GetProperty("pinnedPages").ValueKind);
    }

    [Fact]
    public async Task PinnedPagesAreKeptInTheOrderTheyWereSent_AndComeBackWithTheAccount()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var saved = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/pinned-pages", new { pages = new[] { "calendar", "now", "analytics-group" } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["calendar", "now", "analytics-group"], PagesOf(await ApiTestHost.BodyOf(saved, Ct)));

        // Another sign-in, as on another device, reads the same list.
        var elsewhere = await host.LoginAsync(user.Username, TestAccounts.Password, Ct);
        var me = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, elsewhere, Ct);
        Assert.Equal(["calendar", "now", "analytics-group"], PagesOf(await ApiTestHost.BodyOf(me, Ct)));

        await using var context = db.NewContext();
        var row = await context.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct);
        Assert.Equal(["calendar", "now", "analytics-group"], row.PinnedPages);
    }

    [Fact]
    public async Task AnEmptyListIsKept_SoUnpinningEverythingIsNotTheDefaultsAgain()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, "/api/auth/pinned-pages", new { pages = Array.Empty<string>() }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Empty(PagesOf(await ApiTestHost.BodyOf(saved, Ct)));
    }

    [Fact]
    public async Task SavingAgainReplacesTheList_AndANameSentTwiceIsKeptOnce()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        await host.SendJsonAsync(HttpMethod.Put, "/api/auth/pinned-pages", new { pages = new[] { "now", "live" } }, cookie, Ct);
        var again = await host.SendJsonAsync(
            HttpMethod.Put, "/api/auth/pinned-pages", new { pages = new[] { "bans", "bans", "now" } }, cookie, Ct);

        Assert.Equal(["bans", "now"], PagesOf(await ApiTestHost.BodyOf(again, Ct)));
    }

    [Fact]
    public async Task OnePersonsPinsAreNotAnotherPersons()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, mine) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (_, theirs) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        await host.SendJsonAsync(HttpMethod.Put, "/api/auth/pinned-pages", new { pages = new[] { "live" } }, mine, Ct);

        var me = await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, theirs, Ct);
        Assert.Equal(JsonValueKind.Null, (await ApiTestHost.BodyOf(me, Ct)).GetProperty("pinnedPages").ValueKind);
    }

    [Fact]
    public async Task AListThatIsMissingOrNotPageNamesIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        foreach (var body in new object[]
        {
            new { pages = (string[]?)null },
            new { pages = new[] { "now", "" } },
            new { pages = new[] { "Now Page!" } },
            new { pages = new[] { new string('a', PinnedPages.LongestName + 1) } },
            new { pages = Enumerable.Range(0, PinnedPages.MostPages + 1).Select(i => $"page-{i}").ToArray() },
        })
        {
            var response = await host.SendJsonAsync(HttpMethod.Put, "/api/auth/pinned-pages", body, cookie, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task SettingPinsNeedsASignedInPerson()
    {
        await using var host = await ApiTestHost.StartAsync(db);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/auth/pinned-pages", new { pages = new[] { "now" } }, null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string[] PagesOf(JsonElement me) =>
        [.. me.GetProperty("pinnedPages").EnumerateArray().Select(p => p.GetString()!)];
}

public class PinnedPagesCleanTests
{
    [Fact]
    public void ANameIsTrimmedAndKeptOnceInTheOrderSent()
    {
        Assert.Equal(["live", "now"], PinnedPages.Clean([" live ", "now", "live"]));
    }

    [Fact]
    public void AnEmptyListIsAList()
    {
        Assert.Empty(PinnedPages.Clean([])!);
    }

    [Fact]
    public void NoListAtAllIsRefused()
    {
        Assert.Null(PinnedPages.Clean(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Now")]
    [InlineData("a b")]
    [InlineData("a/b")]
    public void AnythingThatIsNotAPageNameShapeIsRefused(string name)
    {
        Assert.Null(PinnedPages.Clean(["now", name]));
    }

    [Fact]
    public void ANameWithADashAndDigitsIsFine()
    {
        Assert.Equal(["world-lists", "stats-2"], PinnedPages.Clean(["world-lists", "stats-2"]));
    }
}
