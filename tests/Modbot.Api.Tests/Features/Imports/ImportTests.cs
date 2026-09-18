using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Modbot.Api.Features.Imports;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Imports;

/// <summary>
/// Import design: the file format, every mapped kind, unknown kinds kept, re-uploads skipped,
/// rejections with line numbers, dry runs that write nothing, the permission, and the one audit
/// entry per import.
/// </summary>
/// <remarks>
/// The import job runs in the test host's own hosted service, so every test uploads and then
/// waits for the import to finish the way a page would: by asking again.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class ImportTests
{
    private const string Path = "/api/imports";

    private readonly PostgresFixture _db;

    public ImportTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSource() => $"src_{Guid.NewGuid():N}"[..24];

    private static string NewUser() => $"usr_{Guid.NewGuid():N}";

    [Fact]
    public async Task AJsonArray_ImportsEveryMappedKind_OnBothPlatforms()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var vrchat = NewUser();
        var discord = Guid.NewGuid().ToString("N")[..18];

        var expected = new Dictionary<string, (string Platform, string Id, string Type)>
        {
            ["join"] = ("vrchat", vrchat, FactType.MemberJoined),
            ["leave"] = ("vrchat", vrchat, FactType.MemberLeft),
            ["remove"] = ("vrchat", vrchat, FactType.MemberKicked),
            ["kick"] = ("vrchat", vrchat, FactType.GroupInstanceKick),
            ["warn"] = ("vrchat", vrchat, FactType.GroupInstanceWarn),
            ["ban"] = ("vrchat", vrchat, FactType.MemberBanned),
            ["unban"] = ("vrchat", vrchat, FactType.MemberUnbanned),
            ["note"] = ("vrchat", vrchat, FactType.NoteAdded),
            ["role-add"] = ("vrchat", vrchat, FactType.RoleGranted),
            ["role-remove"] = ("vrchat", vrchat, FactType.RoleRevoked),
            ["invite"] = ("vrchat", vrchat, FactType.InviteCreated),
            ["join-request"] = ("vrchat", vrchat, FactType.JoinRequestCreated),
        };

        var discordExpected = new Dictionary<string, string>
        {
            ["join"] = FactType.DiscordMemberJoined,
            ["leave"] = FactType.DiscordMemberLeft,
            ["kick"] = FactType.DiscordMemberKicked,
            ["ban"] = FactType.DiscordMemberBanned,
            ["unban"] = FactType.DiscordMemberUnbanned,
            ["timeout"] = FactType.DiscordMemberTimedOut,
            ["note"] = FactType.NoteAdded,
            ["role-add"] = FactType.DiscordRoleGranted,
            ["role-remove"] = FactType.DiscordRoleRevoked,
        };

        var records = new List<object>();
        var minute = 0;
        foreach (var (kind, (platform, id, _)) in expected)
            records.Add(Record(kind, $"2024-03-01T10:{minute++:00}:00Z", platform, id));
        foreach (var kind in discordExpected.Keys)
            records.Add(Record(kind, $"2024-03-01T11:{minute++:00}:00Z", "discord", discord));

        var import = await UploadAsync(host, cookie, source, JsonSerializer.Serialize(records));
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(records.Count, done.GetProperty("received").GetInt32());
        Assert.Equal(records.Count, done.GetProperty("imported").GetInt32());
        Assert.Equal(0, done.GetProperty("rejected").GetInt32());
        Assert.Equal(0, done.GetProperty("skipped").GetInt32());

        foreach (var (_, (_, id, type)) in expected)
        {
            var fact = Assert.Single(await host.FactsAsync(type, id, Ct));

            // Nothing said where these came from, so they are what an unlabelled import is: a
            // person put them in by hand, from elsewhere.
            Assert.Equal(FactSource.Manual, fact.Source);
            Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
            Assert.Null(fact.TypeRaw);
            Assert.Equal(source, ApiTestHost.DataOf(fact).GetProperty("importSource").GetString());
        }

        foreach (var (_, type) in discordExpected)
        {
            var fact = Assert.Single(await host.FactsAsync(type, discord, Ct));
            Assert.Equal(FactSource.Manual, fact.Source);
            Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        }
    }

    [Fact]
    public async Task NewlineDelimited_ImportsTheSame_AndKeepsTheActorAndData()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var subject = NewUser();
        var actor = NewUser();

        var lines = new StringBuilder()
            .AppendLine(JsonSerializer.Serialize(new
            {
                kind = "ban",
                at = "2024-05-02T08:00:00Z",
                subject = new { platform = "VRChat", id = subject },
                actor = new { platform = "vrchat", id = actor, name = "Alice" },
                externalId = "ban-1",
                data = new { reason = "Harassment" },
            }))
            .AppendLine()
            .AppendLine(JsonSerializer.Serialize(new
            {
                kind = "warn",
                at = "2024-05-02T09:00:00",
                subject = new { platform = "vrchat", id = subject },
            }))
            .ToString();

        var import = await UploadAsync(host, cookie, source, lines, contentType: "application/x-ndjson");
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(2, done.GetProperty("imported").GetInt32());

        var ban = Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
        Assert.Equal(new DateTimeOffset(2024, 5, 2, 8, 0, 0, TimeSpan.Zero), ban.OccurredAt);
        Assert.Null(ban.OccurredBefore);
        Assert.Equal(FactPlatform.VRChat, ban.ActorPlatform);
        Assert.Equal(actor, ban.ActorId);

        var data = ApiTestHost.DataOf(ban);
        Assert.Equal("Harassment", data.GetProperty("reason").GetString());
        Assert.Equal("Alice", data.GetProperty("actorDisplayName").GetString());
        Assert.Equal("ban-1", data.GetProperty("externalId").GetString());
        Assert.Equal(import.GetProperty("id").GetString(), data.GetProperty("importId").GetString());

        // No offset means UTC.
        var warn = Assert.Single(await host.FactsAsync(FactType.GroupInstanceWarn, subject, Ct));
        Assert.Equal(new DateTimeOffset(2024, 5, 2, 9, 0, 0, TimeSpan.Zero), warn.OccurredAt);
    }

    [Fact]
    public async Task AnUnknownKind_IsKept_WithTheKindInTypeRaw_AndAFullTypeName_IsUsedAsIs()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var subject = NewUser();

        var body = JsonSerializer.Serialize(new object[]
        {
            Record("group.leftTheChat", "2024-01-01T00:00:00Z", "vrchat", subject),
            Record("timeout", "2024-01-01T00:01:00Z", "vrchat", subject),
            Record(FactType.GroupPostCreated, "2024-01-01T00:02:00Z", "vrchat", subject),
            Record("vrchat.made.up.type", "2024-01-01T00:03:00Z", "vrchat", subject),
        });

        var import = await UploadAsync(host, cookie, source, body);
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal(4, done.GetProperty("imported").GetInt32());
        Assert.Equal(0, done.GetProperty("rejected").GetInt32());

        var unrecognised = await host.FactsAsync(FactType.Unrecognised, subject, Ct);
        Assert.Equal(
            new[] { "group.leftTheChat", "timeout", "vrchat.made.up.type" }.Order(),
            unrecognised.Select(f => f.TypeRaw).Order());

        Assert.Single(await host.FactsAsync(FactType.GroupPostCreated, subject, Ct));
    }

    [Fact]
    public async Task ReUploadingTheSameFile_SkipsEveryRecord_ByExternalIdAndByHash()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var subject = NewUser();

        var body = JsonSerializer.Serialize(new object[]
        {
            new
            {
                kind = "ban",
                at = "2024-02-01T00:00:00Z",
                subject = new { platform = "vrchat", id = subject },
                externalId = "b-1",
                data = new { reason = "first" },
            },
            new
            {
                kind = "warn",
                at = "2024-02-01T00:01:00Z",
                subject = new { platform = "vrchat", id = subject },
                data = new { b = 2, a = 1 },
            },
            // The same warn again, keys in another order: one fact.
            new
            {
                kind = "warn",
                at = "2024-02-01T00:01:00Z",
                subject = new { platform = "vrchat", id = subject },
                data = new { a = 1, b = 2 },
            },
        });

        var first = await FinishedAsync(host, cookie, (await UploadAsync(host, cookie, source, body)).GetProperty("id").GetString()!);
        Assert.Equal(2, first.GetProperty("imported").GetInt32());
        Assert.Equal(1, first.GetProperty("skipped").GetInt32());

        // The external id wins over a changed body.
        var again = body.Replace("\"first\"", "\"second\"", StringComparison.Ordinal);
        var second = await FinishedAsync(host, cookie, (await UploadAsync(host, cookie, source, again)).GetProperty("id").GetString()!);
        Assert.Equal(0, second.GetProperty("imported").GetInt32());
        Assert.Equal(3, second.GetProperty("skipped").GetInt32());

        Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
        Assert.Single(await host.FactsAsync(FactType.GroupInstanceWarn, subject, Ct));

        // Under another source label it is another record.
        var third = await FinishedAsync(host, cookie, (await UploadAsync(host, cookie, NewSource(), body)).GetProperty("id").GetString()!);
        Assert.Equal(2, third.GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task Rejections_CarryTheLineNumber_AndTheFileGoesOn()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var subject = NewUser();

        var lines = new StringBuilder()
            .AppendLine(JsonSerializer.Serialize(Record("join", "2024-06-01T00:00:00Z", "vrchat", subject)))
            .AppendLine("{ not json")
            .AppendLine(JsonSerializer.Serialize(new { at = "2024-06-01T00:00:00Z", subject = new { platform = "vrchat", id = subject } }))
            .AppendLine(JsonSerializer.Serialize(Record("ban", "2999-01-01T00:00:00Z", "vrchat", subject)))
            .AppendLine(JsonSerializer.Serialize(Record("ban", "2024-06-01T00:00:00Z", "steam", subject)))
            .AppendLine(JsonSerializer.Serialize(new { kind = "ban", at = "2024-06-01T00:00:00Z" }))
            .AppendLine("[1, 2]")
            .AppendLine(JsonSerializer.Serialize(Record("leave", "2024-06-01T01:00:00Z", "vrchat", subject)))
            .ToString();

        var import = await UploadAsync(host, cookie, source, lines);
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(8, done.GetProperty("received").GetInt32());
        Assert.Equal(2, done.GetProperty("imported").GetInt32());
        Assert.Equal(6, done.GetProperty("rejected").GetInt32());

        var rejections = done.GetProperty("rejections").EnumerateArray()
            .ToDictionary(r => r.GetProperty("line").GetInt32(), r => r.GetProperty("reason").GetString()!);

        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, rejections.Keys.Order());
        Assert.StartsWith("Not valid JSON", rejections[2], StringComparison.Ordinal);
        Assert.Equal("kind is missing.", rejections[3]);
        Assert.Equal("at is in the future.", rejections[4]);
        Assert.Equal("subject.platform is not vrchat or discord.", rejections[5]);
        Assert.Equal("subject is missing.", rejections[6]);
        Assert.Equal("Not an object.", rejections[7]);

        Assert.Single(await host.FactsAsync(FactType.MemberJoined, subject, Ct));
        Assert.Single(await host.FactsAsync(FactType.MemberLeft, subject, Ct));
    }

    [Fact]
    public async Task AFileThatIsNotJson_Fails_WithTheReason()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var import = await UploadAsync(host, cookie, NewSource(), "[ { \"kind\": ");
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal("Failed", done.GetProperty("status").GetString());
        Assert.StartsWith("The file is not JSON", done.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRun_CountsEverything_AndWritesNothing()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var subject = NewUser();

        var body = JsonSerializer.Serialize(new object[]
        {
            Record("ban", "2024-02-01T00:00:00Z", "vrchat", subject),
            Record("ban", "2024-02-01T00:00:00Z", "vrchat", subject),
            new { kind = "ban" },
        });

        var import = await UploadAsync(host, cookie, source, body, dryRun: true);
        Assert.True(import.GetProperty("dryRun").GetBoolean());

        var id = import.GetProperty("id").GetString()!;
        var done = await FinishedAsync(host, cookie, id);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(3, done.GetProperty("received").GetInt32());
        Assert.Equal(1, done.GetProperty("imported").GetInt32());
        Assert.Equal(1, done.GetProperty("skipped").GetInt32());
        Assert.Equal(1, done.GetProperty("rejected").GetInt32());

        Assert.Empty(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
        Assert.Empty(await host.FactsAsync(FactType.ImportDone, id, Ct));

        // And a real import afterwards imports it, because the dry run recorded nothing.
        var real = await FinishedAsync(host, cookie, (await UploadAsync(host, cookie, source, body)).GetProperty("id").GetString()!);
        Assert.Equal(1, real.GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task ChangeSettingsIsNotEnough_AndSoIsAnUploadWithoutASource()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        // Import used to ride on Change settings. It has its own permission now, so an operator
        // who holds everything the Settings page needs still cannot write history.
        var (_, settings) = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.ViewAuditLog | ModbotPermissions.Ban, Ct);

        var refused = await UploadRawAsync(host, settings, NewSource(), "[]", dryRun: false, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Path, settings), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, $"{Path}/{Guid.NewGuid()}", settings), Ct)).StatusCode);

        var (_, operatorCookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var noSource = await UploadRawAsync(host, operatorCookie, "", "[]", dryRun: false, contentType: "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);

        var empty = await UploadRawAsync(host, operatorCookie, NewSource(), "", dryRun: false, contentType: "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task AnApiKey_HoldingImportOldData_CanImport_AndTheAuditEntryNamesItsOwner()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (user, cookie) = await host.SignedInAsync(
            ModbotPermissions.ImportOldData | ModbotPermissions.ManageApiKeys, Ct);

        var made = await host.SendJsonAsync(
            HttpMethod.Post, "/api/api-keys", new { name = "Importer", permissions = new[] { "ImportOldData" }, expiresAt = (string?)null }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var key = (await ApiTestHost.BodyOf(made, Ct)).GetProperty("key").GetString()!;

        var source = NewSource();
        var subject = NewUser();
        var body = JsonSerializer.Serialize(new object[]
        {
            Record("ban", "2024-02-01T00:00:00Z", "vrchat", subject),
            new { kind = "ban" },
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Path}?source={source}&fileName=old.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var import = await ApiTestHost.BodyOf(response, Ct);
        var id = import.GetProperty("id").GetString()!;
        Assert.Equal(user.Username, import.GetProperty("startedBy").GetString());
        Assert.Equal("old.json", import.GetProperty("fileName").GetString());

        var status = new HttpRequestMessage(HttpMethod.Get, $"{Path}/{id}");
        status.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(status, Ct)).StatusCode);

        var done = await FinishedAsync(host, cookie, id);
        Assert.Equal("Done", done.GetProperty("status").GetString());

        var entry = Assert.Single(await host.FactsAsync(FactType.ImportDone, id, Ct));
        Assert.Equal(FactSource.Modbot, entry.Source);
        Assert.Equal(FactPlatform.Modbot, entry.SubjectPlatform);
        Assert.Equal(user.Id.ToString(), entry.ActorId);

        var data = ApiTestHost.DataOf(entry);
        Assert.Equal(source, data.GetProperty("source").GetString());
        Assert.Equal("old.json", data.GetProperty("fileName").GetString());
        Assert.Equal("Done", data.GetProperty("status").GetString());
        Assert.Equal(2, data.GetProperty("received").GetInt32());
        Assert.Equal(1, data.GetProperty("imported").GetInt32());
        Assert.Equal(1, data.GetProperty("rejected").GetInt32());
        Assert.Equal(user.Username, data.GetProperty("actorDisplayName").GetString());
    }

    [Fact]
    public async Task AMultipartForm_IsAcceptedToo_AndTheImportIsListed()
    {
        await using var host = await ApiTestHost.StartAsync(_db);

        // ViewAuditLog as well, to read the imported fact back through the audit log at the end.
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData | ModbotPermissions.ViewAuditLog, Ct);

        var source = NewSource();
        var subject = NewUser();
        var body = JsonSerializer.Serialize(new object[] { Record("join", "2024-02-01T00:00:00Z", "vrchat", subject) });

        var form = new MultipartFormDataContent
        {
            { new StringContent(source), "source" },
            { new StringContent("AuditLog"), "seenBy" },
            { new StringContent("false"), "dryRun" },
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", "export.json");

        var request = host.Authenticated(HttpMethod.Post, Path, cookie);
        request.Content = form;

        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var import = await ApiTestHost.BodyOf(response, Ct);
        var id = import.GetProperty("id").GetString()!;
        Assert.Equal("export.json", import.GetProperty("fileName").GetString());

        var done = await FinishedAsync(host, cookie, id);
        Assert.Equal(1, done.GetProperty("imported").GetInt32());
        Assert.Equal("AuditLog", done.GetProperty("seenBy").GetString());

        var list = await ApiTestHost.BodyOf(await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Path, cookie), Ct), Ct);
        Assert.Contains(list.GetProperty("imports").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

        // In the audit log, under the source the upload chose rather than one of its own.
        var audit = await ApiTestHost.BodyOf(
            await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, $"/api/audit/?source=AuditLog&subject={subject}", cookie), Ct), Ct);
        var row = Assert.Single(audit.GetProperty("entries").EnumerateArray());
        Assert.Equal("AuditLog", row.GetProperty("source").GetString());
    }

    private static object Record(string kind, string at, string platform, string id)
        => new { kind, at, subject = new { platform, id } };

    private static async Task<JsonElement> UploadAsync(
        ApiTestHost host, string cookie, string source, string body, bool dryRun = false, string contentType = "application/json")
    {
        var response = await UploadRawAsync(host, cookie, source, body, dryRun, contentType);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static Task<HttpResponseMessage> UploadRawAsync(
        ApiTestHost host, string cookie, string source, string body, bool dryRun, string contentType)
    {
        var request = host.Authenticated(HttpMethod.Post, $"{Path}?source={Uri.EscapeDataString(source)}&dryRun={dryRun}", cookie);
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return host.Client.SendAsync(request, Ct);
    }

    /// <summary>Asks for the import until it is done or failed, the way the page does.</summary>
    private static async Task<JsonElement> FinishedAsync(ApiTestHost host, string cookie, string id)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, $"{Path}/{id}", cookie), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var view = await ApiTestHost.BodyOf(response, Ct);
            var status = view.GetProperty("status").GetString();
            if (status is "Done" or "Failed")
                return view;

            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException($"Import {id} is still {status}.");

            await Task.Delay(100, Ct);
        }
    }
}
