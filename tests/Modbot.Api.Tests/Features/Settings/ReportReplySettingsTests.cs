using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Discord → Report replies: the ready-made messages staff can send a reporter when they
/// close a report. The list is replaced whole, trimmed, and refused when it breaks a limit.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ReportReplySettingsTests
{
    private const string Path = "/api/settings/report-replies";

    private readonly PostgresFixture _db;

    public ReportReplySettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Replies(params (string Name, string Text)[] replies)
        => new { replies = replies.Select(r => new { name = r.Name, text = r.Text }).ToList() };

    [Fact]
    public async Task WithoutChangeSettings_ItIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewReports, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(("Handled", "Thanks.")), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheListIsSavedWhole_TrimmedAndReadBackInOrder()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, Path, Replies((" Handled ", " Thanks, we dealt with it. "), ("No action", "We looked and found nothing.")), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        var replies = body.GetProperty("replies").EnumerateArray().ToList();

        Assert.Equal(2, replies.Count);
        Assert.Equal("Handled", replies[0].GetProperty("name").GetString());
        Assert.Equal("Thanks, we dealt with it.", replies[0].GetProperty("text").GetString());
        Assert.Equal("No action", replies[1].GetProperty("name").GetString());

        // An empty list removes them all.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(), cookie, Ct)).StatusCode);
        Assert.Empty((await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct)).GetProperty("replies").EnumerateArray());
    }

    [Fact]
    public async Task ABrokenList_IsRefused_AndKeepsWhatWasThere()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(("Handled", "Thanks.")), cookie, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(("", "Thanks.")), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(("Handled", "  ")), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(("A", "x"), ("a", "y")), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies((new string('n', ReportReplies.MaxNameLength + 1), "x")), cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendJsonAsync(HttpMethod.Put, Path, Replies(("Long", new string('t', MemberReport.MaxReporterMessageLength + 1))), cookie, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Put, Path, Replies([.. Enumerable.Range(0, ReportReplies.MostKept + 1).Select(i => ($"Reply {i}", "x"))]), cookie, Ct)).StatusCode);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.Equal("Handled", Assert.Single(body.GetProperty("replies").EnumerateArray()).GetProperty("name").GetString());
    }
}
