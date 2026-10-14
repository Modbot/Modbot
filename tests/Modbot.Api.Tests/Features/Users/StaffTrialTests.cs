using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>The new staff checklist: the lines set per server, and a trial's answers, sign-offs and note.</summary>
[Collection(nameof(PostgresCollection))]
public class StaffTrialTests
{
    private const string SettingsPath = "/api/settings/new-staff-checklist";

    private readonly PostgresFixture _db;

    public StaffTrialTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> SetLinesAsync(
        ApiTestHost host, string cookie, string[] tasks, string[] signOffs)
        => host.SendJsonAsync(HttpMethod.Put, SettingsPath, new { tasks, signOffs }, cookie, Ct);

    private static Task<HttpResponseMessage> StartAsync(ApiTestHost host, string cookie, ModbotUser account, ModbotUser buddy)
        => host.SendJsonAsync(HttpMethod.Post, "/api/staff-trials", new { accountId = account.Id, buddyId = buddy.Id }, cookie, Ct);

    private static Task<HttpResponseMessage> ReadAsync(ApiTestHost host, string cookie, ModbotUser account)
        => host.SendJsonAsync(HttpMethod.Get, $"/api/staff-trials/{account.Id}", null, cookie, Ct);

    /// <summary>The trial's lines of one kind, in the order they are shown.</summary>
    private static List<JsonElement> ItemsOf(JsonElement trial, string kind)
        => trial.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == kind).ToList();

    /// <summary>
    /// An admin who has set one task and one sign-off line, a new member on trial with a buddy, and the
    /// first task and first sign-off line of that trial.
    /// </summary>
    private async Task<Started> StartedTrialAsync(ApiTestHost host)
    {
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (person, personCookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (buddy, buddyCookie) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        Assert.Equal(HttpStatusCode.OK, (await SetLinesAsync(host, admin, ["Read the rules"], ["Joined a shift"])).StatusCode);

        var started = await StartAsync(host, admin, person, buddy);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var trial = await ApiTestHost.BodyOf(started, Ct);

        return new Started(
            admin, person, personCookie, buddy, buddyCookie,
            ItemsOf(trial, "task")[0].GetProperty("id").GetGuid(),
            ItemsOf(trial, "sign-off")[0].GetProperty("id").GetGuid());
    }

    private sealed record Started(
        string Admin, ModbotUser Person, string PersonCookie, ModbotUser Buddy, string BuddyCookie, Guid TaskId, Guid SignOffId);

    [Fact]
    public async Task TheLines_NeedManageUsers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ManageSettings, Ct);

        var read = await host.SendJsonAsync(HttpMethod.Get, SettingsPath, null, viewer, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var write = await SetLinesAsync(host, viewer, ["A"], ["B"]);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task SavingTheLines_TrimsThemAndDropsBlankOnes()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        var response = await SetLinesAsync(host, manager, ["  Read the rules  ", "", "   "], ["", "Joined a shift"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(["Read the rules"], body.GetProperty("tasks").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(["Joined a shift"], body.GetProperty("signOffs").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task SavingTheLines_RefusesALongLineAndTooManyLines()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        var tooLong = await SetLinesAsync(host, manager, [new string('x', 301)], []);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var atTheLimit = await SetLinesAsync(host, manager, [new string('x', 300)], []);
        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);

        var tooMany = await SetLinesAsync(host, manager, Enumerable.Range(0, 31).Select(i => $"Task {i}").ToArray(), []);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var allowed = await SetLinesAsync(host, manager, Enumerable.Range(0, 30).Select(i => $"Task {i}").ToArray(), []);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task StartingATrial_CopiesTheLinesAndWritesAFact()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        var t = await StartedTrialAsync(host);

        var read = await ReadAsync(host, t.Admin, t.Person);
        var trial = (await ApiTestHost.BodyOf(read, Ct)).GetProperty("trial");
        Assert.Equal(t.Buddy.Id, trial.GetProperty("buddyId").GetGuid());
        Assert.Equal(host.Clock.UtcNow, trial.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal("Read the rules", ItemsOf(trial, "task").Single().GetProperty("text").GetString());
        Assert.Equal("Joined a shift", ItemsOf(trial, "sign-off").Single().GetProperty("text").GetString());

        var fact = Assert.Single(await host.FactsAsync(FactType.StaffTrialStarted, t.Person.Id.ToString(), Ct));
        Assert.Equal(t.Buddy.Username, ApiTestHost.DataOf(fact).GetProperty("buddy").GetString());
    }

    [Fact]
    public async Task ChangingTheLinesLater_LeavesARunningTrialAlone()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await SetLinesAsync(host, t.Admin, ["A different task"], [])).StatusCode);

        var trial = (await ApiTestHost.BodyOf(await ReadAsync(host, t.Admin, t.Person), Ct)).GetProperty("trial");
        Assert.Equal("Read the rules", ItemsOf(trial, "task").Single().GetProperty("text").GetString());
        Assert.Equal("Joined a shift", ItemsOf(trial, "sign-off").Single().GetProperty("text").GetString());
    }

    [Fact]
    public async Task StartingASecondTrial_Answers409()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        var again = await StartAsync(host, t.Admin, t.Person, t.Buddy);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task TheBuddy_CannotBeTheNewMemberOrADisabledAccount()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (person, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (disabled, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var self = await StartAsync(host, admin, person, person);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var off = await host.SendJsonAsync(HttpMethod.Post, $"/api/users/{disabled.Id}/disable", null, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var gone = await StartAsync(host, admin, person, disabled);
        Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
    }

    [Fact]
    public async Task StartingChangingTheBuddyAndRemoving_NeedManageUsers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var start = await StartAsync(host, t.BuddyCookie, other, t.Buddy);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);

        var change = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{t.Person.Id}/buddy", new { buddyId = other.Id }, t.BuddyCookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);

        var remove = await host.SendJsonAsync(HttpMethod.Delete, $"/api/staff-trials/{t.Person.Id}", null, t.PersonCookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
    }

    /// <summary>Test accounts rank by how many permissions they hold (see RoleOrderTests).</summary>
    [Fact]
    public async Task StartingChangingTheBuddyAndRemoving_OnlyReachAccountsBelowYourHighestRole()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        const ModbotPermissions lead = ModbotPermissions.ManageUsers | ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;
        var (_, leadCookie) = await host.SignedInAsync(lead, Ct);
        var (peer, _) = await host.SignedInAsync(lead, Ct);
        var (above, _) = await host.SignedInAsync(lead | ModbotPermissions.ViewAnalytics, Ct);
        var (below, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (buddy, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        // Not below the lead: refused to start, with the sentence the other account actions use.
        foreach (var outranking in new[] { peer, above })
        {
            var refused = await StartAsync(host, leadCookie, outranking, buddy);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("You can only change accounts below your highest role.", await refused.Content.ReadAsStringAsync(Ct));

            // An administrator starts it; the lead can then neither change its buddy nor take it off.
            Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, admin, outranking, buddy)).StatusCode);

            var change = await host.SendJsonAsync(
                HttpMethod.Put, $"/api/staff-trials/{outranking.Id}/buddy", new { buddyId = other.Id }, leadCookie, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);

            var remove = await host.SendJsonAsync(HttpMethod.Delete, $"/api/staff-trials/{outranking.Id}", null, leadCookie, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        }

        // Below the lead: allowed.
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, leadCookie, below, buddy)).StatusCode);

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{below.Id}/buddy", new { buddyId = other.Id }, leadCookie, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var removed = await host.SendJsonAsync(HttpMethod.Delete, $"/api/staff-trials/{below.Id}", null, leadCookie, Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task TwoStartsAtOnce_AnswerOneOkAndOne409()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (person, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var (buddy, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var results = await Task.WhenAll(
            StartAsync(host, admin, person, buddy),
            StartAsync(host, admin, person, buddy));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task ChangingTheBuddy_KeepsTheSignOffsAndWritesAFact()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var (other, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{t.Person.Id}/items/{t.SignOffId}/sign-off", new { signedOff = true }, t.BuddyCookie, Ct);

        var change = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{t.Person.Id}/buddy", new { buddyId = other.Id }, t.Admin, Ct);

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var trial = await ApiTestHost.BodyOf(change, Ct);
        Assert.Equal(other.Id, trial.GetProperty("buddyId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, ItemsOf(trial, "sign-off").Single().GetProperty("signedOffAt").ValueKind);
        Assert.Single(await host.FactsAsync(FactType.StaffTrialBuddyChanged, t.Person.Id.ToString(), Ct));
    }

    [Fact]
    public async Task OnlyThePersonOnTrialWritesAnAnswer()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var path = $"/api/staff-trials/{t.Person.Id}/items/{t.TaskId}/answer";

        foreach (var cookie in new[] { t.BuddyCookie, t.Admin })
        {
            var refused = await host.SendJsonAsync(HttpMethod.Put, path, new { answer = "Not mine" }, cookie, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var written = await host.SendJsonAsync(HttpMethod.Put, path, new { answer = "  Be kind.  " }, t.PersonCookie, Ct);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var task = ItemsOf((await ApiTestHost.BodyOf(written, Ct)), "task").Single();
        Assert.Equal("Be kind.", task.GetProperty("answer").GetString());

        var cleared = await host.SendJsonAsync(HttpMethod.Put, path, new { answer = "" }, t.PersonCookie, Ct);
        Assert.Equal(JsonValueKind.Null, ItemsOf(await ApiTestHost.BodyOf(cleared, Ct), "task").Single().GetProperty("answer").ValueKind);

        var tooLong = await host.SendJsonAsync(HttpMethod.Put, path, new { answer = new string('x', 4001) }, t.PersonCookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task TheBuddyAndAManager_SignOff_AndTheyWriteTheNote()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var signOff = $"/api/staff-trials/{t.Person.Id}/items/{t.SignOffId}/sign-off";

        var byBuddy = await host.SendJsonAsync(HttpMethod.Put, signOff, new { signedOff = true }, t.BuddyCookie, Ct);
        Assert.Equal(HttpStatusCode.OK, byBuddy.StatusCode);
        var line = ItemsOf(await ApiTestHost.BodyOf(byBuddy, Ct), "sign-off").Single();
        Assert.Equal(t.Buddy.Username, line.GetProperty("signedOffBy").GetString());

        var takenBack = await host.SendJsonAsync(HttpMethod.Put, signOff, new { signedOff = false }, t.Admin, Ct);
        Assert.Equal(HttpStatusCode.OK, takenBack.StatusCode);
        Assert.Equal(
            JsonValueKind.Null,
            ItemsOf(await ApiTestHost.BodyOf(takenBack, Ct), "sign-off").Single().GetProperty("signedOffAt").ValueKind);

        // One fact for the sign-off and one for taking it back; repeating the same state writes nothing.
        await host.SendJsonAsync(HttpMethod.Put, signOff, new { signedOff = false }, t.Admin, Ct);
        Assert.Equal(2, (await host.FactsAsync(FactType.StaffTrialSignedOff, t.Person.Id.ToString(), Ct)).Count);

        var note = $"/api/staff-trials/{t.Person.Id}/note";
        var byManager = await host.SendJsonAsync(HttpMethod.Put, note, new { note = "Settled in well." }, t.Admin, Ct);
        Assert.Equal(HttpStatusCode.OK, byManager.StatusCode);
        Assert.Equal("Settled in well.", (await ApiTestHost.BodyOf(byManager, Ct)).GetProperty("buddyNote").GetString());
    }

    [Fact]
    public async Task ThePersonOnTrial_CannotSignOffOrWriteTheNote_EvenHoldingManageUsers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (person, personCookie) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);
        var (buddy, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        await SetLinesAsync(host, admin, [], ["Joined a shift"]);
        var trial = await ApiTestHost.BodyOf(await StartAsync(host, admin, person, buddy), Ct);
        var lineId = ItemsOf(trial, "sign-off").Single().GetProperty("id").GetGuid();

        var signOff = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{person.Id}/items/{lineId}/sign-off", new { signedOff = true }, personCookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signOff.StatusCode);

        var note = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{person.Id}/note", new { note = "I did well." }, personCookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, note.StatusCode);
    }

    [Fact]
    public async Task AStranger_CannotSignOffOrWriteTheNote()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var (_, stranger) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var signOff = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{t.Person.Id}/items/{t.SignOffId}/sign-off", new { signedOff = true }, stranger, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signOff.StatusCode);

        var note = await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{t.Person.Id}/note", new { note = "Hello" }, stranger, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, note.StatusCode);
    }

    [Fact]
    public async Task ReadingATrial_IsForThePerson_TheBuddy_AndWhoeverSeesTheAccountSide()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var (_, logReader) = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var (_, stranger) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        foreach (var cookie in new[] { t.PersonCookie, t.BuddyCookie, logReader, t.Admin })
        {
            var allowed = await ReadAsync(host, cookie, t.Person);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.NotEqual(JsonValueKind.Null, (await ApiTestHost.BodyOf(allowed, Ct)).GetProperty("trial").ValueKind);
        }

        var refused = await ReadAsync(host, stranger, t.Person);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // A stranger is not even told whether an account has a trial.
        var (noTrial, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);
        var unknown = await ReadAsync(host, stranger, noTrial);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
    }

    [Fact]
    public async Task MyTrials_ListsMyOwnAndTheOnesIAmBuddyTo()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        var asPerson = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/staff-trials/mine", null, t.PersonCookie, Ct), Ct);
        Assert.Equal(t.Person.Id, asPerson.GetProperty("own").GetProperty("accountId").GetGuid());
        Assert.Empty(asPerson.GetProperty("asBuddy").EnumerateArray());

        var asBuddy = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/staff-trials/mine", null, t.BuddyCookie, Ct), Ct);
        Assert.Equal(JsonValueKind.Null, asBuddy.GetProperty("own").ValueKind);
        Assert.Equal(t.Person.Id, asBuddy.GetProperty("asBuddy").EnumerateArray().Single().GetProperty("accountId").GetGuid());
    }

    [Fact]
    public async Task RemovingATrial_DeletesItAndWritesAFact()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        var removed = await host.SendJsonAsync(HttpMethod.Delete, $"/api/staff-trials/{t.Person.Id}", null, t.Admin, Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var read = await ApiTestHost.BodyOf(await ReadAsync(host, t.Admin, t.Person), Ct);
        Assert.Equal(JsonValueKind.Null, read.GetProperty("trial").ValueKind);
        Assert.Single(await host.FactsAsync(FactType.StaffTrialRemoved, t.Person.Id.ToString(), Ct));
    }

    [Fact]
    public async Task DeletingTheAccount_DeletesItsTrialAndAnswers()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        await host.SendJsonAsync(
            HttpMethod.Put, $"/api/staff-trials/{t.Person.Id}/items/{t.TaskId}/answer", new { answer = "Mine" }, t.PersonCookie, Ct);

        var deleted = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/users/{t.Person.Id}/delete", new { username = t.Person.Username }, t.Admin, Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.False(await db.StaffTrials.AnyAsync(x => x.AccountId == t.Person.Id, Ct));
        Assert.False(await db.StaffTrialItems.AnyAsync(x => x.Answer == "Mine", Ct));
    }

    private static Task<HttpResponseMessage> CloseAsync(ApiTestHost host, string cookie, ModbotUser account)
        => host.SendJsonAsync(HttpMethod.Post, $"/api/staff-trials/{account.Id}/close", null, cookie, Ct);

    [Fact]
    public async Task ClosingATrial_StampsItKeepsTheLinesAndWritesAFact()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        var closed = await CloseAsync(host, t.Admin, t.Person);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var view = await ApiTestHost.BodyOf(closed, Ct);
        Assert.Equal(host.Clock.UtcNow, view.GetProperty("closedAt").GetDateTimeOffset());
        Assert.Single(ItemsOf(view, "task"));
        Assert.Single(await host.FactsAsync(FactType.StaffTrialClosed, t.Person.Id.ToString(), Ct));

        var read = (await ApiTestHost.BodyOf(await ReadAsync(host, t.Admin, t.Person), Ct)).GetProperty("trial");
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("closedAt").ValueKind);
    }

    [Fact]
    public async Task ClosingATrialTwice_KeepsTheFirstDateAndWritesOneFact()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        var first = await ApiTestHost.BodyOf(await CloseAsync(host, t.Admin, t.Person), Ct);
        host.Clock.Advance(TimeSpan.FromDays(1));
        var second = await CloseAsync(host, t.Admin, t.Person);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            first.GetProperty("closedAt").GetDateTimeOffset(),
            (await ApiTestHost.BodyOf(second, Ct)).GetProperty("closedAt").GetDateTimeOffset());
        Assert.Single(await host.FactsAsync(FactType.StaffTrialClosed, t.Person.Id.ToString(), Ct));
    }

    [Fact]
    public async Task ClosingATrial_NeedsManageUsers_AndATrial()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (noTrial, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(host, viewer, t.Person)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(host, t.BuddyCookie, t.Person)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CloseAsync(host, t.Admin, noTrial)).StatusCode);
        Assert.Empty(await host.FactsAsync(FactType.StaffTrialClosed, t.Person.Id.ToString(), Ct));
    }

    [Fact]
    public async Task ClosingATrial_RefusesAnAccountThatIsNotBelowYourHighestRole()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        const ModbotPermissions lead = ModbotPermissions.ManageUsers | ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;
        var (_, leadCookie) = await host.SignedInAsync(lead, Ct);
        var (above, _) = await host.SignedInAsync(lead | ModbotPermissions.ViewAnalytics, Ct);
        var (below, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (buddy, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, admin, above, buddy)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, admin, below, buddy)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await CloseAsync(host, leadCookie, above)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(host, leadCookie, below)).StatusCode);
    }

    [Fact]
    public async Task StartingATrialOnYourOwnAccount_Answers400()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (me, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (buddy, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        var started = await StartAsync(host, admin, me, buddy);

        Assert.Equal(HttpStatusCode.BadRequest, started.StatusCode);
        Assert.Empty(await host.FactsAsync(FactType.StaffTrialStarted, me.Id.ToString(), Ct));
    }

    [Fact]
    public async Task CanStartAndCanManage_FollowRank_AndNeverOfferYourOwnAccount()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        const ModbotPermissions lead = ModbotPermissions.ManageUsers | ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;
        var (leadUser, leadCookie) = await host.SignedInAsync(lead, Ct);
        var (peer, _) = await host.SignedInAsync(lead, Ct);
        var (below, _) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (buddy, _) = await host.SignedInAsync(ModbotPermissions.None, Ct);

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, admin, peer, buddy)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, admin, below, buddy)).StatusCode);

        var peerRead = await ApiTestHost.BodyOf(await ReadAsync(host, leadCookie, peer), Ct);
        Assert.False(peerRead.GetProperty("canStart").GetBoolean());
        Assert.False(peerRead.GetProperty("trial").GetProperty("canManage").GetBoolean());

        var belowRead = await ApiTestHost.BodyOf(await ReadAsync(host, leadCookie, below), Ct);
        Assert.True(belowRead.GetProperty("canStart").GetBoolean());
        Assert.True(belowRead.GetProperty("trial").GetProperty("canManage").GetBoolean());

        var ownRead = await ApiTestHost.BodyOf(await ReadAsync(host, leadCookie, leadUser), Ct);
        Assert.False(ownRead.GetProperty("canStart").GetBoolean());
    }

    [Fact]
    public async Task TheReminderChannel_IsSavedWithTheLines_AndBlankTurnsItOff()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        var saved = await host.SendJsonAsync(
            HttpMethod.Put, SettingsPath, new { tasks = new[] { "A" }, signOffs = new[] { "B" }, reminderChannelId = " 1234567890 " }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var read = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, SettingsPath, null, manager, Ct), Ct);
        Assert.Equal("1234567890", read.GetProperty("reminderChannelId").GetString());

        var off = await host.SendJsonAsync(
            HttpMethod.Put, SettingsPath, new { tasks = new[] { "A" }, signOffs = new[] { "B" }, reminderChannelId = "  " }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var after = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, SettingsPath, null, manager, Ct), Ct);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("reminderChannelId").ValueKind);
    }

    [Fact]
    public async Task MyTrials_LeavesAClosedTrialOutOfTheBuddiesList_ButNotThePersonsOwn()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var t = await StartedTrialAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await CloseAsync(host, t.Admin, t.Person)).StatusCode);

        var asBuddy = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/staff-trials/mine", null, t.BuddyCookie, Ct), Ct);
        Assert.Empty(asBuddy.GetProperty("asBuddy").EnumerateArray());

        var asPerson = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, "/api/staff-trials/mine", null, t.PersonCookie, Ct), Ct);
        Assert.Equal(t.Person.Id, asPerson.GetProperty("own").GetProperty("accountId").GetGuid());
    }

    [Fact]
    public async Task TheReminderChannel_IsRefusedAboveTheWidthItIsKeptIn()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, manager) = await host.SignedInAsync(ModbotPermissions.ManageUsers, Ct);

        var tooLong = await host.SendJsonAsync(
            HttpMethod.Put, SettingsPath, new { tasks = new[] { "A" }, signOffs = new[] { "B" }, reminderChannelId = new string('1', 33) }, manager, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var atTheLimit = await host.SendJsonAsync(
            HttpMethod.Put, SettingsPath, new { tasks = new[] { "A" }, signOffs = new[] { "B" }, reminderChannelId = new string('1', 32) }, manager, Ct);
        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
    }
}
