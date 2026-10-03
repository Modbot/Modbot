using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Google Calendar (Google Calendar design §3.1, step 1): the key is kept encrypted and
/// never returned, a file that is not a key is refused, every change is audited, and Check signs in
/// and reads, and nothing else.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GoogleCalendarSettingsTests
{
    private const string Path = "/api/settings/google-calendar";
    private const string CalendarId = "c_abc123@group.calendar.google.com";

    private readonly PostgresFixture _db;

    public GoogleCalendarSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, FakeGoogle Google, string Cookie)> StartAsync()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var google = new FakeGoogle();
        var host = await ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(GoogleCalendarClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => google));

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        return (host, google, cookie);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task SetUpAsync(ApiTestHost host, string cookie, string keyFile)
    {
        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { keyFile, calendarId = CalendarId }, cookie, Ct));
    }

    [Fact]
    public async Task TheKeyIsStoredEncrypted_AndNeverReturned()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        var pem = GoogleKeyFile.Parse(keyFile, out _)!.PrivateKeyPem;
        var secretLine = pem.Split('\n')[1];

        var saved = await host.SendJsonAsync(HttpMethod.Put, Path, new { keyFile, calendarId = CalendarId }, cookie, Ct);
        var savedText = await saved.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(secretLine, savedText, StringComparison.Ordinal);

        var read = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        var readText = await read.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(secretLine, readText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", readText, StringComparison.Ordinal);

        var body = JsonDocument.Parse(readText).RootElement;
        Assert.True(body.GetProperty("keyStored").GetBoolean());
        Assert.Equal("modbot@test-project.iam.gserviceaccount.com", body.GetProperty("address").GetString());
        Assert.Equal(CalendarId, body.GetProperty("calendarId").GetString());

        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.DoesNotContain(secretLine, settings.GooglePrivateKeyEncrypted, StringComparison.Ordinal);
        Assert.Equal(pem, host.Services.GetRequiredService<ISecretProtector>().Unprotect(settings.GooglePrivateKeyEncrypted));
    }

    [Fact]
    public async Task AFileThatIsNotAKeyIsRefused()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var (wrongKind, rsa) = FakeGoogle.KeyFile(type: "authorized_user");
        using var keyPair = rsa;

        foreach (var keyFile in new[] { wrongKind, "{}", "not json" })
        {
            var response = await host.SendJsonAsync(HttpMethod.Put, Path, new { keyFile }, cookie, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("This is not a Google key file.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        }

        await using var context = _db.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).GooglePrivateKeyEncrypted);
    }

    [Fact]
    public async Task ACalendarIdIsTakenFromAPastedLink()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path,
            new { calendarId = "https://calendar.google.com/calendar/embed?src=c_abc123%40group.calendar.google.com&ctz=Europe%2FLondon" },
            cookie, Ct));

        Assert.Equal(CalendarId, body.GetProperty("calendarId").GetString());
    }

    [Fact]
    public async Task CheckSignsInAndReads_AndSendsNothingElse()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        // One token request, then two reads: nothing is written to Google.
        Assert.Equal(3, google.Requests.Count);
        Assert.Equal(HttpMethod.Post, google.Requests[0].Method);
        Assert.Equal(new Uri("https://oauth2.googleapis.com/token"), google.Requests[0].Uri);
        Assert.All(google.Requests.Skip(1), r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("www.googleapis.com", r.Uri.Host);
        });
        Assert.EndsWith("/events", google.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/acl", google.Requests[2].Uri.AbsolutePath, StringComparison.Ordinal);

        var check = body.GetProperty("check");
        Assert.Equal("Group events", check.GetProperty("calendarName").GetString());
        Assert.Equal("Europe/London", check.GetProperty("timeZone").GetString());
        Assert.True(check.GetProperty("canChangeEvents").GetBoolean());
        Assert.Equal("all", check.GetProperty("public").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("problem").ValueKind);

        var links = body.GetProperty("links");
        Assert.Equal("https://calendar.google.com/calendar/r?cid=c_abc123%40group.calendar.google.com", links.GetProperty("subscribe").GetString());
        Assert.Equal("https://calendar.google.com/calendar/ical/c_abc123%40group.calendar.google.com/public/basic.ics", links.GetProperty("iCal").GetString());
    }

    [Fact]
    public async Task TheLinksAreShownOnlyForACalendarPublicWithDetails()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        google.PublicRole = "freeBusyReader";

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        Assert.Equal("freeBusy", body.GetProperty("check").GetProperty("public").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("links").ValueKind);
    }

    [Theory]
    [InlineData("reader", "Modbot can only read this calendar.")]
    [InlineData("freeBusyReader", "Modbot can't see this calendar.")]
    public async Task CheckSaysWhenModbotCannotChangeEvents(string role, string problem)
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        google.AccessRole = role;

        var check = (await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct))).GetProperty("check");

        Assert.False(check.GetProperty("canChangeEvents").GetBoolean());
        Assert.Equal(problem, check.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task CheckSaysWhenModbotCannotSeeTheCalendar()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        google.CalendarStatus = HttpStatusCode.NotFound;

        var check = (await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct))).GetProperty("check");

        Assert.Equal("Modbot can't see this calendar.", check.GetProperty("problem").GetString());
        Assert.Equal(2, google.Requests.Count);
    }

    [Fact]
    public async Task ARefusedKeySaysSo()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        google.TokenStatus = HttpStatusCode.BadRequest;

        var check = (await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct))).GetProperty("check");

        Assert.Equal("Google did not accept the key.", check.GetProperty("problem").GetString());
        Assert.Single(google.Requests);
    }

    [Fact]
    public async Task ASharingListModbotMayNotReadLeavesPublicUnknown()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        google.AclStatus = HttpStatusCode.Forbidden;

        var check = (await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct))).GetProperty("check");

        Assert.Equal("unknown", check.GetProperty("public").GetString());
        Assert.True(check.GetProperty("canChangeEvents").GetBoolean());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("problem").ValueKind);
    }

    /// <summary>CLAUDE.md: never retry a 429. A limit stops every call, Check included, until it ends.</summary>
    [Fact]
    public async Task ALimitStopsEveryCallUntilItEnds()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        google.CalendarStatus = HttpStatusCode.TooManyRequests;
        google.CalendarError = """{"error":{"code":429,"message":"Rate Limit Exceeded","errors":[{"reason":"rateLimitExceeded"}]}}""";

        var limited = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.Equal("Google is limiting Modbot.", limited.GetProperty("check").GetProperty("problem").GetString());
        Assert.Equal(host.Clock.UtcNow.AddMinutes(15), limited.GetProperty("limitedUntil").GetDateTimeOffset());
        var sent = google.Requests.Count;

        // Pressed again inside the 15 minutes: nothing goes to Google.
        google.CalendarStatus = HttpStatusCode.OK;
        host.Clock.Advance(TimeSpan.FromMinutes(14));
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.Equal(sent, google.Requests.Count);

        // Once it has passed, Check goes again.
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        var again = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.True(google.Requests.Count > sent);
        Assert.Equal(JsonValueKind.Null, again.GetProperty("limitedUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, again.GetProperty("check").GetProperty("problem").ValueKind);
    }

    [Fact]
    public async Task CheckNeedsAKeyAndACalendar()
    {
        var (host, google, cookie) = await StartAsync();
        await using var running = host;

        var noKey = await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { keyFile }, cookie, Ct));

        var noCalendar = await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noCalendar.StatusCode);
        Assert.Empty(google.Requests);
    }

    [Fact]
    public async Task ChangingTheCalendarClearsWhatCheckFound()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { calendarId = "other@group.calendar.google.com" }, cookie, Ct));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("check").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("links").ValueKind);
        Assert.True(body.GetProperty("keyStored").GetBoolean());
    }

    [Fact]
    public async Task ForgetRemovesTheKeyTheCalendarAndWhatCheckFound()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        await SetUpAsync(host, cookie, keyFile);
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Delete, Path, null, cookie, Ct));

        Assert.False(body.GetProperty("keyStored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("address").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("calendarId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("check").ValueKind);

        await using var context = _db.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).GooglePrivateKeyEncrypted);
    }

    [Fact]
    public async Task EveryChangeIsAudited_AndTheKeyOnlyAsChanged()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        var (keyFile, rsa) = FakeGoogle.KeyFile();
        using var keyPair = rsa;
        var secretLine = GoogleKeyFile.Parse(keyFile, out _)!.PrivateKeyPem.Split('\n')[1];

        await SetUpAsync(host, cookie, keyFile);
        await OkAsync(await host.SendJsonAsync(HttpMethod.Delete, Path, null, cookie, Ct));

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        var google = facts.Select(ApiTestHost.DataOf).Where(d => d.GetProperty("setting").GetString() == "googleCalendar").ToList();

        Assert.Equal(2, google.Count);
        Assert.All(google, d =>
        {
            Assert.True(d.GetProperty("changed").GetProperty("googleKey").GetProperty("secret").GetBoolean());
            Assert.DoesNotContain(secretLine, d.GetRawText(), StringComparison.Ordinal);
        });
        Assert.Contains(google, d => d.GetProperty("changed").GetProperty("googleCalendarId").GetProperty("new").GetString() == CalendarId);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/check")]
    public async Task WithoutTheSettingsPermission_EveryEndpointIsRefused(string method, string suffix)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var body = method == "PUT" ? new { calendarId = CalendarId } : null;

        var signedIn = await host.SendJsonAsync(new HttpMethod(method), Path + suffix, body, cookie, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signedIn.StatusCode);

        var anonymous = await host.SendJsonAsync(new HttpMethod(method), Path + suffix, body, null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
