using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Imports;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Imports;

/// <summary>
/// Import design: the file format, every mapped kind, unknown kinds kept, re-uploads skipped,
/// records mapped onto each source and a source Modbot does not have refused, facts Modbot
/// already knows skipped without collapsing distinct events, an imported fact traceable back to
/// its import, rejections with line numbers, dry runs that write nothing, the permission, and the
/// one audit entry per import.
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
        Assert.Equal(0, first.GetProperty("alreadyKnown").GetInt32());

        // The external id wins over a changed body.
        var again = body.Replace("\"first\"", "\"second\"", StringComparison.Ordinal);
        var second = await FinishedAsync(host, cookie, (await UploadAsync(host, cookie, source, again)).GetProperty("id").GetString()!);
        Assert.Equal(0, second.GetProperty("imported").GetInt32());
        Assert.Equal(3, second.GetProperty("skipped").GetInt32());
        Assert.Equal(0, second.GetProperty("alreadyKnown").GetInt32());

        Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
        Assert.Single(await host.FactsAsync(FactType.GroupInstanceWarn, subject, Ct));

        // Under another source label the key is new, so every record is looked at again -- and
        // every one of them is an event Modbot now has. Nothing is written twice.
        var third = await FinishedAsync(host, cookie, (await UploadAsync(host, cookie, NewSource(), body)).GetProperty("id").GetString()!);
        Assert.Equal(0, third.GetProperty("imported").GetInt32());
        Assert.Equal(1, third.GetProperty("skipped").GetInt32());
        Assert.Equal(2, third.GetProperty("alreadyKnown").GetInt32());

        Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
        Assert.Single(await host.FactsAsync(FactType.GroupInstanceWarn, subject, Ct));
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

    [Fact]
    public async Task EveryAllowedSource_CanBeChosenPerRecord_AndTheUploadSetsTheDefault()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var records = new List<object>();
        var subjects = new Dictionary<FactSource, string>();

        var minute = 0;
        foreach (var seenBy in ImportSources.All)
        {
            var subject = NewUser();
            subjects[seenBy] = subject;
            records.Add(new
            {
                kind = "ban",
                at = $"2024-07-01T09:{minute++:00}:00Z",
                subject = new { platform = "vrchat", id = subject },
                seenBy = seenBy.ToString(),
            });
        }

        // No seenBy of its own: the upload's choice stands.
        var inherits = NewUser();
        records.Add(Record("ban", "2024-07-01T09:59:00Z", "vrchat", inherits));

        var import = await UploadAsync(
            host, cookie, source, JsonSerializer.Serialize(records), seenBy: nameof(FactSource.Discord));
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(records.Count, done.GetProperty("imported").GetInt32());
        Assert.Equal(0, done.GetProperty("rejected").GetInt32());
        Assert.Equal(nameof(FactSource.Discord), done.GetProperty("seenBy").GetString());

        foreach (var (seenBy, subject) in subjects)
        {
            var fact = Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
            Assert.Equal(seenBy, fact.Source);
        }

        Assert.Equal(
            FactSource.Discord,
            Assert.Single(await host.FactsAsync(FactType.MemberBanned, inherits, Ct)).Source);

        // Import is not one of them, and never becomes one by accident.
        Assert.DoesNotContain(FactSource.Import, ImportSources.All);
    }

    [Fact]
    public async Task NoSourceAnywhere_IsManual()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var subject = NewUser();
        var body = JsonSerializer.Serialize(new object[]
        {
            Record("ban", "2024-08-01T00:00:00Z", "vrchat", subject),
        });

        var import = await UploadAsync(host, cookie, NewSource(), body);
        var done = await FinishedAsync(host, cookie, import.GetProperty("id").GetString()!);

        Assert.Equal(1, done.GetProperty("imported").GetInt32());
        Assert.Equal(nameof(FactSource.Manual), done.GetProperty("seenBy").GetString());

        // A person put this in, by hand, from somewhere else. That is what Manual means.
        Assert.Equal(
            FactSource.Manual,
            Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct)).Source);
    }

    [Fact]
    public async Task ASourceModbotDoesNotHave_IsRefused_OnTheUploadAndOnARecord()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var subject = NewUser();

        var nonsense = await UploadRawAsync(
            host, cookie, NewSource(), "[]", dryRun: false, contentType: "application/json", seenBy: "Wherever");
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);

        // Import used to be the one source every imported fact carried. It is a legacy value now.
        var legacy = await UploadRawAsync(
            host, cookie, NewSource(), "[]", dryRun: false, contentType: "application/json", seenBy: "Import");
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        Assert.Contains(
            "no longer a source",
            (await ApiTestHost.BodyOf(legacy, Ct)).GetProperty("error").GetString(),
            StringComparison.Ordinal);

        var lines = new StringBuilder()
            .AppendLine(JsonSerializer.Serialize(new
            {
                kind = "ban",
                at = "2024-09-01T00:00:00Z",
                subject = new { platform = "vrchat", id = subject },
                seenBy = "Wherever",
            }))
            .AppendLine(JsonSerializer.Serialize(new
            {
                kind = "ban",
                at = "2024-09-01T00:01:00Z",
                subject = new { platform = "vrchat", id = subject },
                seenBy = "Import",
            }))
            .AppendLine(JsonSerializer.Serialize(new
            {
                kind = "ban",
                at = "2024-09-01T00:02:00Z",
                subject = new { platform = "vrchat", id = subject },
                // Case does not matter.
                seenBy = "auditlog",
            }))
            .ToString();

        var done = await FinishedAsync(
            host, cookie, (await UploadAsync(host, cookie, NewSource(), lines)).GetProperty("id").GetString()!);

        Assert.Equal(3, done.GetProperty("received").GetInt32());
        Assert.Equal(1, done.GetProperty("imported").GetInt32());
        Assert.Equal(2, done.GetProperty("rejected").GetInt32());

        var rejections = done.GetProperty("rejections").EnumerateArray()
            .ToDictionary(r => r.GetProperty("line").GetInt32(), r => r.GetProperty("reason").GetString()!);

        Assert.Contains("is not a source", rejections[1], StringComparison.Ordinal);
        Assert.Contains("no longer a source", rejections[2], StringComparison.Ordinal);

        Assert.Equal(
            FactSource.AuditLog,
            Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct)).Source);
    }

    [Fact]
    public async Task AFactModbotAlreadyHas_IsAlreadyKnown_AndIsStillTraceableToTheImport()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var subject = NewUser();
        var at = new DateTimeOffset(2024, 10, 5, 14, 0, 0, TimeSpan.Zero);

        // Modbot read this ban from VRChat's own audit log while it happened. The old bot's
        // export has it too, worded its own way.
        var existing = await WriteExistingFactAsync(host, FactType.MemberBanned, subject, at);

        var body = JsonSerializer.Serialize(new object[]
        {
            new
            {
                kind = "ban",
                at = "2024-10-05T14:00:00Z",
                subject = new { platform = "vrchat", id = subject },
                externalId = "ban-77",
                seenBy = nameof(FactSource.AuditLog),
                data = new { reason = "the old bot's wording, which will never match" },
            },
        });

        var done = await FinishedAsync(
            host, cookie, (await UploadAsync(host, cookie, source, body)).GetProperty("id").GetString()!);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("received").GetInt32());
        Assert.Equal(0, done.GetProperty("imported").GetInt32());
        Assert.Equal(1, done.GetProperty("alreadyKnown").GetInt32());
        Assert.Equal(0, done.GetProperty("skipped").GetInt32());

        // One ban, not two.
        var fact = Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));
        Assert.Equal(existing, fact.Id);

        // And the record is still traceable: its row points at the fact that already said it.
        var row = Assert.Single(await ImportRecordsAsync(host, source));
        Assert.Equal("id:ban-77", row.Key);
        Assert.Equal(existing, row.FactId);
        Assert.Equal(done.GetProperty("id").GetString(), row.ImportId.ToString());

        // A second upload of the same file has nothing to do at all.
        var again = await FinishedAsync(
            host, cookie, (await UploadAsync(host, cookie, source, body)).GetProperty("id").GetString()!);
        Assert.Equal(0, again.GetProperty("imported").GetInt32());
        Assert.Equal(1, again.GetProperty("skipped").GetInt32());
        Assert.Equal(0, again.GetProperty("alreadyKnown").GetInt32());
    }

    [Fact]
    public async Task TwoDistinctEventsAtAlmostTheSameTime_BothSurvive()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var oneSecondApart = NewUser();
        var sameMinute = NewUser();

        var body = JsonSerializer.Serialize(new object[]
        {
            // A second apart: a client report's five-second window would have merged these.
            new
            {
                kind = "warn",
                at = "2024-11-02T20:00:00Z",
                subject = new { platform = "vrchat", id = oneSecondApart },
                externalId = "w-1",
                data = new { reason = "Mic spam" },
            },
            new
            {
                kind = "warn",
                at = "2024-11-02T20:00:01Z",
                subject = new { platform = "vrchat", id = oneSecondApart },
                externalId = "w-2",
                data = new { reason = "Told to stop and did not" },
            },

            // The very same moment, because the old spreadsheet only kept the day. Three
            // warnings are three warnings; telling them apart is the external id's job.
            new
            {
                kind = "warn",
                at = "2024-11-03T00:00:00Z",
                subject = new { platform = "vrchat", id = sameMinute },
                externalId = "w-3",
                data = new { reason = "First" },
            },
            new
            {
                kind = "warn",
                at = "2024-11-03T00:00:00Z",
                subject = new { platform = "vrchat", id = sameMinute },
                externalId = "w-4",
                data = new { reason = "Second" },
            },
            new
            {
                kind = "warn",
                at = "2024-11-03T00:00:00Z",
                subject = new { platform = "vrchat", id = sameMinute },
                externalId = "w-5",
                data = new { reason = "Third" },
            },
        });

        var done = await FinishedAsync(
            host, cookie, (await UploadAsync(host, cookie, source, body)).GetProperty("id").GetString()!);

        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal(5, done.GetProperty("imported").GetInt32());
        Assert.Equal(0, done.GetProperty("alreadyKnown").GetInt32());
        Assert.Equal(0, done.GetProperty("skipped").GetInt32());

        Assert.Equal(2, (await host.FactsAsync(FactType.GroupInstanceWarn, oneSecondApart, Ct)).Count);
        Assert.Equal(3, (await host.FactsAsync(FactType.GroupInstanceWarn, sameMinute, Ct)).Count);
    }

    [Fact]
    public async Task ADryRun_CountsWhatModbotAlreadyKnows_WithoutWritingAnything()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ImportOldData, Ct);

        var source = NewSource();
        var known = NewUser();
        var fresh = NewUser();
        var at = new DateTimeOffset(2024, 12, 9, 11, 30, 0, TimeSpan.Zero);

        await WriteExistingFactAsync(host, FactType.MemberBanned, known, at);

        var body = JsonSerializer.Serialize(new object[]
        {
            Record("ban", "2024-12-09T11:30:00Z", "vrchat", known),
            Record("ban", "2024-12-09T11:30:00Z", "vrchat", fresh),
        });

        var dry = await FinishedAsync(
            host, cookie, (await UploadAsync(host, cookie, source, body, dryRun: true)).GetProperty("id").GetString()!);

        Assert.Equal(1, dry.GetProperty("imported").GetInt32());
        Assert.Equal(1, dry.GetProperty("alreadyKnown").GetInt32());
        Assert.Empty(await ImportRecordsAsync(host, source));
        Assert.Empty(await host.FactsAsync(FactType.MemberBanned, fresh, Ct));

        // The real run does exactly what the dry run said it would.
        var real = await FinishedAsync(
            host, cookie, (await UploadAsync(host, cookie, source, body)).GetProperty("id").GetString()!);

        Assert.Equal(1, real.GetProperty("imported").GetInt32());
        Assert.Equal(1, real.GetProperty("alreadyKnown").GetInt32());
        Assert.Single(await host.FactsAsync(FactType.MemberBanned, fresh, Ct));
        Assert.Single(await host.FactsAsync(FactType.MemberBanned, known, Ct));
    }

    [Fact]
    public async Task AnImportedFact_NamesTheImportThatWroteIt_WhateverSourceItCarries()
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
                at = "2025-01-04T16:45:00Z",
                subject = new { platform = "vrchat", id = subject },
                externalId = "ban-2001",
                seenBy = nameof(FactSource.AuditLog),
                data = new { reason = "Harassment" },
            },
        });

        var import = await UploadAsync(host, cookie, source, body, fileName: "old-bot.json");
        var id = import.GetProperty("id").GetString()!;
        var done = await FinishedAsync(host, cookie, id);
        Assert.Equal(1, done.GetProperty("imported").GetInt32());

        var fact = Assert.Single(await host.FactsAsync(FactType.MemberBanned, subject, Ct));

        // The source says VRChat's audit log, which is where the event happened. Where the claim
        // came from is on the fact itself, which is the whole point of moving it there.
        Assert.Equal(FactSource.AuditLog, fact.Source);

        var data = ApiTestHost.DataOf(fact);
        Assert.Equal(id, data.GetProperty("importId").GetString());
        Assert.Equal(source, data.GetProperty("importSource").GetString());
        Assert.Equal("ban-2001", data.GetProperty("externalId").GetString());

        var row = Assert.Single(await ImportRecordsAsync(host, source));
        Assert.Equal(fact.Id, row.FactId);
        Assert.Equal(id, row.ImportId.ToString());
        Assert.Equal(subject, row.SubjectId);
    }

    private static object Record(string kind, string at, string platform, string id)
        => new { kind, at, subject = new { platform, id } };

    private static async Task<JsonElement> UploadAsync(
        ApiTestHost host,
        string cookie,
        string source,
        string body,
        bool dryRun = false,
        string contentType = "application/json",
        string? seenBy = null,
        string? fileName = null)
    {
        var response = await UploadRawAsync(host, cookie, source, body, dryRun, contentType, seenBy, fileName);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static Task<HttpResponseMessage> UploadRawAsync(
        ApiTestHost host,
        string cookie,
        string source,
        string body,
        bool dryRun,
        string contentType,
        string? seenBy = null,
        string? fileName = null)
    {
        var query = $"{Path}?source={Uri.EscapeDataString(source)}&dryRun={dryRun}";
        if (seenBy is not null)
            query += $"&seenBy={Uri.EscapeDataString(seenBy)}";
        if (fileName is not null)
            query += $"&fileName={Uri.EscapeDataString(fileName)}";

        var request = host.Authenticated(HttpMethod.Post, query, cookie);
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return host.Client.SendAsync(request, Ct);
    }

    /// <summary>A fact Modbot recorded itself, so an import can meet one it already has.</summary>
    private static async Task<long> WriteExistingFactAsync(
        ApiTestHost host, string type, string subject, DateTimeOffset at)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(at, Ct);

        var written = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = subject,
                Source = FactSource.AuditLog,
                Data = new JsonObject { ["reason"] = "VRChat's own wording" },
            },
            Ct);

        return written.Id;
    }

    private static async Task<List<ImportRecord>> ImportRecordsAsync(ApiTestHost host, string source)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.ImportRecords.AsNoTracking().Where(r => r.Source == source).ToListAsync(Ct);
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
