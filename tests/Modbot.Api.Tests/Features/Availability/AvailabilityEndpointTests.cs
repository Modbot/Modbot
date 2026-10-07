using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Availability;

/// <summary>
/// The availability API (availability design §3): Enter availability reads and replaces a person's
/// own week and nothing else, See availability reads the team's, neither implies the other, and the
/// team is everyone who may enter times and is neither disabled nor deleted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AvailabilityEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Enter = ModbotPermissions.EnterAvailability;
    private const ModbotPermissions View = ModbotPermissions.ViewAvailability;

    private const string Mine = "/api/availability/mine";
    private const string Team = "/api/availability";

    private static object Week(string zone = "Europe/London", params object[] cells)
        => new { timeZone = zone, cells };

    private static object Cell(int day, int hour, string state = "free") => new { day, hour, state };

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    private static async Task<JsonElement> PersonOf(ApiTestHost host, string viewer, Guid id)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, Team, null, viewer, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var people = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("people").EnumerateArray();
        return people.Single(p => p.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task NotSignedIn_IsRefusedEverywhere()
    {
        await using var host = await ApiTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, Mine, null, null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Put, Mine, Week(), null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendJsonAsync(HttpMethod.Get, Team, null, null, Ct)).StatusCode);
    }

    /// <summary>
    /// The two permissions stand apart: entering your own times is not seeing everybody's, and seeing
    /// everybody's is not a way to enter your own. The server says so whatever the sidebar shows.
    /// </summary>
    [Fact]
    public async Task EnterAndViewAreSeparate_AndNeitherImpliesTheOther()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, enterOnly) = await host.SignedInAsync(Enter, Ct);
        var (_, viewOnly) = await host.SignedInAsync(View, Ct);
        var (_, neither) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Mine, null, enterOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Mine, Week(), enterOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Team, null, enterOnly, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Team, null, viewOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Mine, null, viewOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Mine, Week(), viewOnly, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Mine, null, neither, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Mine, Week(), neither, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Team, null, neither, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnAdministrator_MayDoAll()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Mine, null, admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Mine, Week(), admin, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, Team, null, admin, Ct)).StatusCode);
    }

    [Fact]
    public async Task ANewcomersWeek_IsEmptyWithNoZone()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, cookie) = await host.SignedInAsync(Enter, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, Mine, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("timeZone").ValueKind);
        Assert.Empty(body.GetProperty("cells").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("savedAt").ValueKind);
    }

    [Fact]
    public async Task SavingAWeek_KeepsItInThePersonsOwnZone_AndReadsBack()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (user, cookie) = await host.SignedInAsync(Enter, Ct);

        var saved = await host.SendJsonAsync(
            HttpMethod.Put, Mine, Week("Asia/Kolkata", Cell(1, 18), Cell(1, 19, "ifNeeded"), Cell(6, 0)), cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var read = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Mine, null, cookie, Ct), Ct);
        Assert.Equal("Asia/Kolkata", read.GetProperty("timeZone").GetString());
        Assert.Equal(host.Clock.UtcNow, read.GetProperty("savedAt").GetDateTimeOffset());

        var cells = read.GetProperty("cells").EnumerateArray()
            .Select(c => (c.GetProperty("day").GetInt32(), c.GetProperty("hour").GetInt32(), c.GetProperty("state").GetString()))
            .ToList();

        Assert.Equal([(1, 18, "free"), (1, 19, "ifNeeded"), (6, 0, "free")], cells);

        await using var context = db.NewContext();
        Assert.Equal(3, await context.StaffAvailabilities.CountAsync(a => a.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheWeek_AndSavingNothingClearsIt()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (user, cookie) = await host.SignedInAsync(Enter, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/London", Cell(0, 9), Cell(0, 10), Cell(2, 20)), cookie, Ct);
        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("America/New_York", Cell(3, 21)), cookie, Ct);

        await using (var context = db.NewContext())
        {
            var rows = await context.StaffAvailabilities.Where(a => a.UserId == user.Id).ToListAsync(Ct);
            var only = Assert.Single(rows);
            Assert.Equal((3, 21), (only.Day, only.Hour));
            Assert.Equal("America/New_York", (await context.StaffAvailabilityZones.SingleAsync(z => z.UserId == user.Id, Ct)).TimeZone);
        }

        var cleared = await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/London"), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        await using var after = db.NewContext();
        Assert.Equal(0, await after.StaffAvailabilities.CountAsync(a => a.UserId == user.Id, Ct));
    }

    /// <summary>A save replaces the caller's own week and nobody else's, whatever the body says.</summary>
    [Fact]
    public async Task SavingChangesOnlyTheCallersOwnWeek()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (other, otherCookie) = await host.SignedInAsync(Enter, Ct);
        var (_, cookie) = await host.SignedInAsync(Enter, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/Paris", Cell(4, 12)), otherCookie, Ct);

        // A body that names somebody else's account changes nothing of theirs: the account is the session's.
        var response = await host.SendJsonAsync(
            HttpMethod.Put, Mine, new { timeZone = "Europe/London", userId = other.Id, cells = new[] { Cell(5, 5) } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = db.NewContext();
        var theirs = Assert.Single(await context.StaffAvailabilities.Where(a => a.UserId == other.Id).ToListAsync(Ct));
        Assert.Equal((4, 12), (theirs.Day, theirs.Hour));
        Assert.Equal("Europe/Paris", (await context.StaffAvailabilityZones.SingleAsync(z => z.UserId == other.Id, Ct)).TimeZone);
    }

    [Theory]
    [InlineData("Not/AZone", "That time zone is not known.")]
    [InlineData("", "Choose a time zone.")]
    public async Task AZoneThatIsMissingOrUnknown_IsRefusedInPlainWords(string zone, string said)
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (user, cookie) = await host.SignedInAsync(Enter, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Mine, Week(zone, Cell(0, 0)), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(said, await ErrorOf(response));

        await using var context = db.NewContext();
        Assert.False(await context.StaffAvailabilities.AnyAsync(a => a.UserId == user.Id, Ct));
        Assert.False(await context.StaffAvailabilityZones.AnyAsync(z => z.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task AnyBadHour_RefusesTheWholeSave_AndLeavesTheOldWeekAlone()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (user, cookie) = await host.SignedInAsync(Enter, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/London", Cell(0, 9)), cookie, Ct);

        var bad = new (object Body, string Said)[]
        {
            (Week("Europe/London", Cell(7, 0)), "A day must be 0 to 6, Monday first."),
            (Week("Europe/London", Cell(-1, 0)), "A day must be 0 to 6, Monday first."),
            (Week("Europe/London", Cell(0, 24)), "An hour must be 0 to 23."),
            (Week("Europe/London", Cell(0, -1)), "An hour must be 0 to 23."),
            (Week("Europe/London", Cell(0, 0, "maybe")), "A state must be free or ifNeeded."),
            (Week("Europe/London", Cell(1, 1), Cell(1, 1, "ifNeeded")), "Each hour can be set once."),
        };

        foreach (var (body, said) in bad)
        {
            var response = await host.SendJsonAsync(HttpMethod.Put, Mine, body, cookie, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(said, await ErrorOf(response));
        }

        await using var context = db.NewContext();
        var kept = Assert.Single(await context.StaffAvailabilities.Where(a => a.UserId == user.Id).ToListAsync(Ct));
        Assert.Equal((0, 9), (kept.Day, kept.Hour));
    }

    [Fact]
    public async Task AWholeWeekIsAllowed_AndOneMoreIsNot()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, cookie) = await host.SignedInAsync(Enter, Ct);

        var all = Enumerable.Range(0, 7)
            .SelectMany(day => Enumerable.Range(0, 24).Select(hour => Cell(day, hour)))
            .ToArray();

        Assert.Equal(168, all.Length);
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/London", all), cookie, Ct)).StatusCode);

        var tooMany = await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/London", [.. all, Cell(0, 0)]), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("At most 168 hours.", await ErrorOf(tooMany));
    }

    [Fact]
    public async Task TheTeam_IsEveryoneWhoMayEnterTimes_WithTheirRolesZonesAndHours()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (viewer, viewerCookie) = await host.SignedInAsync(View, Ct);
        var (entrant, entrantCookie) = await host.SignedInAsync(Enter | ModbotPermissions.ViewMembers, Ct);
        var (admin, adminCookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (silent, _) = await host.SignedInAsync(Enter, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Asia/Kolkata", Cell(1, 18), Cell(1, 19, "ifNeeded")), entrantCookie, Ct);
        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Pacific/Auckland", Cell(6, 7)), adminCookie, Ct);

        var entrantRow = await PersonOf(host, viewerCookie, entrant.Id);
        Assert.Equal(entrant.Username, entrantRow.GetProperty("name").GetString());
        Assert.Equal("Asia/Kolkata", entrantRow.GetProperty("timeZone").GetString());
        Assert.Equal(2, entrantRow.GetProperty("cells").GetArrayLength());
        Assert.Single(entrantRow.GetProperty("roles").EnumerateArray());

        var adminRow = await PersonOf(host, viewerCookie, admin.Id);
        Assert.Equal("Pacific/Auckland", adminRow.GetProperty("timeZone").GetString());

        // Somebody who has saved nothing is still on the list, with no zone and no hours.
        var silentRow = await PersonOf(host, viewerCookie, silent.Id);
        Assert.Equal(JsonValueKind.Null, silentRow.GetProperty("timeZone").ValueKind);
        Assert.Empty(silentRow.GetProperty("cells").EnumerateArray());

        // Seeing the team is not being on it.
        var people = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Team, null, viewerCookie, Ct), Ct))
            .GetProperty("people").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(viewer.Id, people);
    }

    [Fact]
    public async Task TheTeam_LeavesOutDisabledAndDeletedAccounts()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, viewerCookie) = await host.SignedInAsync(View, Ct);
        var (disabled, _) = await host.SignedInAsync(Enter, Ct);
        var (deleted, _) = await host.SignedInAsync(Enter, Ct);
        var (kept, _) = await host.SignedInAsync(Enter, Ct);

        await using (var context = db.NewContext())
        {
            (await context.Users.SingleAsync(u => u.Id == disabled.Id, Ct)).IsDisabled = true;

            var gone = await context.Users.SingleAsync(u => u.Id == deleted.Id, Ct);
            gone.IsDisabled = true;
            gone.DeletedAt = host.Clock.UtcNow;

            await context.SaveChangesAsync(Ct);
        }

        var people = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Team, null, viewerCookie, Ct), Ct))
            .GetProperty("people").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(kept.Id, people);
        Assert.DoesNotContain(disabled.Id, people);
        Assert.DoesNotContain(deleted.Id, people);
    }

    [Fact]
    public async Task TheTeam_LeavesOutSomeoneWhoCannotEnterTimes()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, viewerCookie) = await host.SignedInAsync(View, Ct);
        var (viewOnly, _) = await host.SignedInAsync(View, Ct);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile, Ct);

        var people = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Team, null, viewerCookie, Ct), Ct))
            .GetProperty("people").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();

        Assert.DoesNotContain(viewOnly.Id, people);
        Assert.DoesNotContain(other.Id, people);
    }

    [Fact]
    public async Task DeletingAnAccount_TakesItsWeekWithIt()
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (user, cookie) = await host.SignedInAsync(Enter, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Mine, Week("Europe/London", Cell(2, 2)), cookie, Ct);

        await using var context = db.NewContext();
        await context.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(Ct);

        Assert.False(await context.StaffAvailabilities.AnyAsync(a => a.UserId == user.Id, Ct));
        Assert.False(await context.StaffAvailabilityZones.AnyAsync(z => z.UserId == user.Id, Ct));
    }
}
