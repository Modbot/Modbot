using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Modbot.Api.Features.Companion.Events;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// Ingest, and the two failure modes that are silent: deduplication that does not happen, and
/// events reaching a group they were never meant to reach.
/// </summary>
/// <remarks>
/// Both would leave everything apparently working. Six clients reporting one join would make every
/// time-spent metric wrong by six, and a cross-group leak would quietly accumulate one community's
/// data in another's database. Hence the weight of tests here.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class IngestTests
{
    private readonly PostgresFixture _db;

    public IngestTests(PostgresFixture db) => _db = db;

    private const string Group = "grp_cats";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static CompanionEventDto Event(
        string type = "InstanceJoined",
        string subject = "usr_8f2c",
        string instance = "39911",
        string group = Group,
        DateTimeOffset? at = null,
        string? displayName = "Rin")
        => new(
            Guid.NewGuid().ToString("n"),
            type,
            at ?? Noon,
            null,
            subject,
            "wrld_4b34",
            instance,
            group,
            displayName is null ? null : new Dictionary<string, string> { ["displayName"] = displayName });

    private static EventBatchDto Batch(params CompanionEventDto[] events)
        => new(Guid.NewGuid().ToString("n"), "2026.9.0", -412, "good", events);

    private async Task<(CompanionApiTestHost Host, string Token)> ReadyAsync(CancellationToken ct)
    {
        await CompanionApiTestHost.ResetAsync(_db, ct);
        var host = await CompanionApiTestHost.StartAsync(_db);
        await host.ConfigureGroupAsync(_db, Group, ct);

        return (host, await host.PairDeviceAsync(ct));
    }

    private static async Task<EventBatchResponse> PostAsync(
        CompanionApiTestHost host, string? token, EventBatchDto batch, CancellationToken ct)
    {
        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = JsonContent.Create(batch);

        var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<EventBatchResponse>(ct))!;
    }

    [Fact]
    public async Task AcceptsABatchAndRecordsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var result = await PostAsync(host, token, Batch(Event(), Event(subject: "usr_aa")), ct);

        Assert.Equal(2, result.Accepted);
        Assert.Equal(0, result.Deduplicated);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public async Task SixModeratorsReportingTheSameJoinProduceOneFact()
    {
        // The load-bearing property of the whole subsystem. Its failure mode is silent: nothing
        // errors, and every time-spent metric is simply wrong by six.
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var accepted = 0;
        var deduplicated = 0;

        for (var moderator = 0; moderator < 6; moderator++)
        {
            var token = await host.PairDeviceAsync(ct);

            // Each client saw the same arrival a second or two apart, as six clocks would report
            // it. All six are the same event.
            var result = await PostAsync(
                host, token, Batch(Event(at: Noon.AddSeconds(moderator))), ct);

            accepted += result.Accepted;
            deduplicated += result.Deduplicated;
        }

        Assert.Equal(1, accepted);
        Assert.Equal(5, deduplicated);
    }

    [Fact]
    public async Task AGenuineRejoinIsNotDeduplicatedAway()
    {
        // The window has to be narrow enough to preserve somebody leaving and coming back, which
        // is what makes reporting in corrected server time a prerequisite rather than a nicety.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await PostAsync(host, token, Batch(Event(at: Noon)), ct);
        var second = await PostAsync(host, token, Batch(Event(at: Noon.AddSeconds(15))), ct);

        Assert.Equal(1, second.Accepted);
        Assert.Equal(0, second.Deduplicated);
    }

    [Fact]
    public async Task AnEventForAnotherGroupIsRejectedByIndexRatherThanStored()
    {
        // The client is meant to have decided routing locally and never sent it. One arriving is
        // a client bug or a hostile caller, and either way it is named rather than quietly
        // dropped.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var result = await PostAsync(
            host, token, Batch(Event(), Event(subject: "usr_leak", group: "grp_dogs")), ct);

        Assert.Equal(1, result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(1, rejected.Index);
        Assert.Equal("unknown_group", rejected.Reason);
    }

    [Fact]
    public async Task PresenceObservedIsRecordedAsItsOwnTypeAndNotAsAnArrival()
    {
        // Recorded as a join, one moderator walking into an instance would become forty arrivals, and
        // every time-spent metric would follow it.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await PostAsync(host, token, Batch(Event(type: "InstancePresenceObserved")), ct);

        await using var context = _db.NewContext();
        var fact = Assert.Single(context.Events.ToList());

        Assert.Equal(Core.Data.Entities.FactType.InstancePresenceObserved, fact.Type);
        Assert.NotEqual(Core.Data.Entities.FactType.InstanceJoined, fact.Type);
    }

    [Fact]
    public async Task EveryFactRecordsWhichDeviceReportedIt()
    {
        // What makes a compromised or misbehaving client identifiable, and its facts revocable as
        // a set rather than one at a time.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await PostAsync(host, token, Batch(Event()), ct);

        var device = Assert.Single(await host.Devices.ListDevicesAsync(ct));

        await using var context = _db.NewContext();
        Assert.Contains(device.Id.ToString(), Assert.Single(context.Events.ToList()).Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheDeclaredPayloadFieldsAreStored()
    {
        // An ingest endpoint that writes whatever it is handed is a storage surface for anybody
        // holding a device token.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var smuggled = new CompanionEventDto(
            "e1", "InstanceJoined", Noon, null, "usr_8f2c", "wrld_4b34", "39911", Group,
            new Dictionary<string, string>
            {
                ["displayName"] = "Rin",
                ["rawLogLine"] = "2026.09.12 12:00:00 Log - [Behaviour] OnPlayerJoined Rin",
                ["screenshot"] = "data:image/png;base64,AAAA",
            });

        await PostAsync(host, token, Batch(smuggled), ct);

        await using var context = _db.NewContext();
        var data = Assert.Single(context.Events.ToList()).Data;

        Assert.Contains("Rin", data, StringComparison.Ordinal);
        Assert.DoesNotContain("rawLogLine", data, StringComparison.Ordinal);
        Assert.DoesNotContain("screenshot", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServerStampsItsOwnObservedAtRegardlessOfWhatTheClientClaims()
    {
        // A moderator's PC with a wrong clock must not be able to reorder the log.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await PostAsync(host, token, Batch(Event(at: Noon.AddMinutes(-3))), ct);

        await using var context = _db.NewContext();
        var fact = Assert.Single(context.Events.ToList());

        Assert.Equal(Noon.AddMinutes(-3), fact.OccurredAt);
        Assert.Equal(host.Clock.UtcNow, fact.ObservedAt);
    }

    [Theory]
    [InlineData(-400 * 24 * 60)]
    [InlineData(60 * 24 * 60)]
    public async Task AWildlyWrongClientClockIsClampedAndFlaggedRatherThanRejected(int minutesOff)
    {
        // The failure this prevents is quiet and total. An unclamped timestamp lands outside the
        // fact log's monthly partitions and fails the insert; the client reads 5xx as server
        // trouble and retries forever, so its buffer fills while nothing is ever recorded --
        // indistinguishable, from the moderator's side, from Modbot simply not working.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var result = await PostAsync(host, token, Batch(Event(at: Noon.AddMinutes(minutesOff))), ct);
        Assert.Equal(1, result.Accepted);

        await using var context = _db.NewContext();
        var fact = Assert.Single(context.Events.ToList());

        Assert.InRange(
            fact.OccurredAt,
            host.Clock.UtcNow - EventsHandler.MaxBackdate,
            host.Clock.UtcNow + EventsHandler.MaxSkewAhead);

        // The correction is visible in the data rather than being a silent rewrite.
        Assert.Contains("clockClamped", fact.Data, StringComparison.Ordinal);
        Assert.Contains("claimedOccurredAt", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlausibleTimestampIsNotFlagged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await PostAsync(host, token, Batch(Event(at: Noon.AddHours(-2))), ct);

        await using var context = _db.NewContext();
        Assert.DoesNotContain("clockClamped", Assert.Single(context.Events.ToList()).Data, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-real-token")]
    public async Task AMissingOrWrongTokenIs401AndIsTerminal(string? token)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = JsonContent.Create(Batch(Event()));

        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("device_token_invalid", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevokedTokenStopsWorkingImmediately()
    {
        // Revocation is server-side and immediate. A revoked moderator's client must stop, and be
        // seen to stop, rather than keep reporting until something expires.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await PostAsync(host, token, Batch(Event()), ct);

        var device = Assert.Single(await host.Devices.ListDevicesAsync(ct));
        Assert.True(await host.Devices.RevokeAsync(device.Id, host.Clock.UtcNow, ct));

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = JsonContent.Create(Batch(Event(subject: "usr_after")));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public async Task AnEmptyBatchIs400SoTheClientDropsItRatherThanRetryingForever()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = JsonContent.Create(Batch());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public async Task AnOversizedBatchIs413SoTheClientHalvesAndRetries()
    {
        // Not a 400: nothing is wrong with the events, and dropping them would lose presence that
        // cannot be filled in later.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = JsonContent.Create(Batch(
            [.. Enumerable.Range(0, EventsHandler.MaxEventsPerBatch + 1).Select(i => Event(subject: $"usr_{i}"))]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.SendAsync(request, ct)).StatusCode);
    }

    private static ByteArrayContent Gzipped(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(payload);

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    /// <summary>
    /// The companion gzips every batch over about 4 KB. Until 2026-09-27 nothing here undid that,
    /// the framework answered a bare 400, and the companion dropped the batch -- every arrival
    /// burst was lost.
    /// </summary>
    [Fact]
    public async Task AGzippedBatchIsReadLikeAPlainOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var batch = Batch([.. Enumerable.Range(0, 40).Select(i => Event(subject: $"usr_{i}"))]);
        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = Gzipped(JsonSerializer.SerializeToUtf8Bytes(batch, JsonSerializerOptions.Web));

        var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<EventBatchResponse>(ct))!;
        Assert.Equal(40, result.Accepted);
        Assert.Empty(result.Rejected);
    }

    /// <summary>
    /// A body the server cannot read is Modbot's own 400, with a code. The companion takes a bare
    /// 400 to a gzipped batch as a server too old to read gzip, so this one must not be bare.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadableBodyIs400WithACode(bool gzip)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var junk = Encoding.UTF8.GetBytes("{ this is not a batch");
        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = gzip
            ? Gzipped(junk)
            : new ByteArrayContent(junk) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };

        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("batch_malformed", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyClaimingGzipThatIsNotIs400WithACode()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not gzip at all"));
        request.Content.Headers.ContentEncoding.Add("gzip");

        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("batch_malformed", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGzipThatExpandsPastTheCapIs413()
    {
        // A small body that expands to gigabytes is stopped while reading, not after.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = Gzipped(new byte[EventBatchReader.MaxDecompressedBytes + 1]);

        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("batch_too_large", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGzippedBodyWithoutAValidTokenIs401()
    {
        // The body is read only once the token is checked, so nobody without one can make the
        // server decompress anything.
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", "not-a-real-token");
        request.Content = Gzipped(new byte[EventBatchReader.MaxDecompressedBytes + 1]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownEventTypeIsRejectedByIndexRatherThanGuessedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var result = await PostAsync(host, token, Batch(Event(type: "SomethingNewerThanThisServer")), ct);

        Assert.Equal(0, result.Accepted);
        Assert.Equal("malformed_event", Assert.Single(result.Rejected).Reason);
    }

    /// <summary>
    /// An unknown type costs that one event, not the batch -- which is what lets a newer client
    /// send LogStopped to an older server safely.
    /// </summary>
    [Fact]
    public async Task AnUnknownTypeDoesNotCostTheRestOfTheBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var result = await PostAsync(
            host, token, Batch(Event(), Event(type: "SomethingNewerThanThisServer", subject: "usr_mod")), ct);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, Assert.Single(result.Rejected).Index);
    }

    [Fact]
    public async Task AStoppedLogIsRecordedUnderItsOwnType()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var result = await PostAsync(host, token, Batch(Event(type: "LogStopped", subject: "usr_mod")), ct);

        Assert.Equal(1, result.Accepted);
        Assert.Empty(result.Rejected);

        await using var context = _db.NewContext();
        var fact = context.Events.Single(e => e.SubjectId == "usr_mod");
        Assert.Equal(Core.Data.Entities.FactType.InstanceLogStopped, fact.Type);
        Assert.Equal("39911", fact.InstanceId);
    }

    /// <summary>
    /// The People page's "Last seen by Modbot" and the audit log agree the moment the report is
    /// in: the row moves as the fact is written, not on the profile sync's next pass (which this
    /// host does not run at all). A stopped log is the report that was left out until 2026-09-29.
    /// </summary>
    [Fact]
    public async Task AStoppedLogCountsAsSeeingTheModeratorAsSoonAsItIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var moderator = $"usr_mod_{Guid.NewGuid():N}";
        var stoppedAt = Noon.AddMinutes(-20);

        await PostAsync(host, token, Batch(Event(type: "LogStopped", subject: moderator, at: stoppedAt)), ct);

        await using var context = _db.NewContext();
        var fact = context.Events.Single(e => e.SubjectId == moderator);
        var row = context.VRChatUsers.Single(u => u.UserId == moderator);

        Assert.Equal(fact.OccurredAt, row.LastSeenAt);
        Assert.Equal(stoppedAt, row.LastSeenAt);
        Assert.Equal(stoppedAt, row.FirstSeenAt);
    }

    [Fact]
    public async Task ReportingUpdatesLastSeenSoAnOperatorCanTellWhoIsActuallyCovering()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        Assert.Null(Assert.Single(await host.Devices.ListDevicesAsync(ct)).LastSeenAt);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await PostAsync(host, token, Batch(Event()), ct);

        Assert.Equal(host.Clock.UtcNow, Assert.Single(await host.Devices.ListDevicesAsync(ct)).LastSeenAt);
    }

    // ── A device is only as good as its owner (DeviceStanding) ────────────────────────────────

    /// <summary>What the events endpoint answers this token, with a batch nobody else has sent.</summary>
    private static async Task<HttpStatusCode> StatusAsync(
        CompanionApiTestHost host, string token, CancellationToken ct, string? version = null)
    {
        var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        request.Content = JsonContent.Create(Batch(Event(subject: $"usr_{Guid.NewGuid():N}")));
        if (version is not null)
            request.Headers.Add("X-Modbot-Companion-Version", version);

        return (await host.Client.SendAsync(request, ct)).StatusCode;
    }

    [Fact]
    public async Task ADeviceStopsWhenItsOwnerIsDisabled()
    {
        // The whole reason for the check: a removed moderator's PC must stop reading who is flagged.
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.PairCompanion, ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, ct);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, token, ct));

        await using (var db = host.Database)
        {
            db.Users.Single(u => u.Id == owner.Id).IsDisabled = true;
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host, token, ct));
    }

    [Fact]
    public async Task ADeviceStopsWhenItsOwnerIsDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.PairCompanion, ct);
        var (token, deviceId) = await host.PairDeviceToAsync(owner.Id, ct);

        await using (var db = host.Database)
        {
            var accounts = new Core.Users.UserAccountService(
                db, new Microsoft.AspNetCore.Identity.PasswordHasher<Core.Data.Entities.ModbotUser>(), host.Clock);
            await accounts.DeleteAsync((await accounts.FindAsync(owner.Id, ct))!, ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host, token, ct));
        Assert.NotNull((await host.Devices.FindByIdAsync(deviceId, ct))!.RevokedAt);
    }

    [Fact]
    public async Task ADeviceStopsWhenItsOwnersRoleLosesThePermission_AndWorksAgainWhenItComesBack()
    {
        // Nothing is revoked by a role change: the check reads the roles on every request, so the
        // companion follows the role both ways, as a browser session does.
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.None, ct);
        var roleId = Guid.NewGuid();

        await using (var db = host.Database)
        {
            var name = $"pairing_{roleId:N}";
            db.Roles.Add(new Core.Data.Entities.ModbotRole
            {
                Id = roleId,
                Name = name,
                NameNormalized = Core.Data.Entities.ModbotRole.Normalize(name),
                Permissions = Core.Data.Entities.ModbotPermissions.PairCompanion,
                CreatedAt = host.Clock.UtcNow,
            });
            db.UserRoles.Add(new Core.Data.Entities.ModbotUserRole { UserId = owner.Id, RoleId = roleId });
            await db.SaveChangesAsync(ct);
        }

        var (token, _) = await host.PairDeviceToAsync(owner.Id, ct);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, token, ct));

        await SetRolePermissionsAsync(host, roleId, Core.Data.Entities.ModbotPermissions.ViewMembers, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host, token, ct));

        await SetRolePermissionsAsync(host, roleId, Core.Data.Entities.ModbotPermissions.PairCompanion, ct);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, token, ct));
    }

    private static async Task SetRolePermissionsAsync(
        CompanionApiTestHost host, Guid roleId, Core.Data.Entities.ModbotPermissions permissions, CancellationToken ct)
    {
        await using var db = host.Database;
        db.Roles.Single(r => r.Id == roleId).Permissions = permissions;
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task ADeviceOnTheBuiltInModeratorRoleWorks()
    {
        // A companion paired before this version, to a moderator on the built-in role, keeps
        // working: the migration gave that role "Pair a companion".
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.None, ct);
        await using (var db = host.Database)
        {
            db.UserRoles.Add(new Core.Data.Entities.ModbotUserRole
            {
                UserId = owner.Id,
                RoleId = Core.Data.Entities.BuiltInRoles.ModeratorId,
            });
            await db.SaveChangesAsync(ct);
        }

        var (token, _) = await host.PairDeviceToAsync(owner.Id, ct);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(host, token, ct));
    }

    [Theory]
    [InlineData(91, false)]
    [InlineData(89, true)]
    public async Task ADeviceUnusedForNinetyDaysIsRefused(int daysUnused, bool works)
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.PairCompanion, ct);
        var (token, deviceId) = await host.PairDeviceToAsync(
            owner.Id,
            ct,
            issuedAt: host.Clock.UtcNow.AddDays(-365),
            lastSeenAt: host.Clock.UtcNow.AddDays(-daysUnused));

        Assert.Equal(works ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, await StatusAsync(host, token, ct));

        // A refused device is not touched, so it stays refused.
        if (!works)
        {
            Assert.Equal(
                host.Clock.UtcNow.AddDays(-daysUnused),
                (await host.Devices.FindByIdAsync(deviceId, ct))!.LastSeenAt);
        }
    }

    [Fact]
    public async Task ADeviceNeverSeenCountsFromWhenItWasPaired()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.PairCompanion, ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, ct, issuedAt: host.Clock.UtcNow.AddDays(-91));

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host, token, ct));
    }

    [Fact]
    public async Task LastSeenIsWrittenAtMostOnceEveryFiveMinutes_OrWhenTheVersionChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        var owner = await host.CreateOwnerAsync(Core.Data.Entities.ModbotPermissions.PairCompanion, ct);
        var (token, deviceId) = await host.PairDeviceToAsync(owner.Id, ct);

        async Task<DateTimeOffset?> LastSeenAsync() => (await host.Devices.FindByIdAsync(deviceId, ct))!.LastSeenAt;

        await StatusAsync(host, token, ct, version: "2026.9.0");
        var first = host.Clock.UtcNow;
        Assert.Equal(first, await LastSeenAsync());

        host.Clock.Advance(TimeSpan.FromMinutes(4));
        await StatusAsync(host, token, ct, version: "2026.9.0");
        Assert.Equal(first, await LastSeenAsync());

        // A new version is written at once, so settings never shows the build it replaced.
        await StatusAsync(host, token, ct, version: "2026.9.1");
        Assert.Equal(host.Clock.UtcNow, await LastSeenAsync());
        Assert.Equal("2026.9.1", (await host.Devices.FindByIdAsync(deviceId, ct))!.CompanionVersion);

        var second = host.Clock.UtcNow;
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await StatusAsync(host, token, ct, version: "2026.9.1");
        Assert.NotEqual(second, await LastSeenAsync());
        Assert.Equal(host.Clock.UtcNow, await LastSeenAsync());
    }

    [Fact]
    public async Task ACompanionCanRemoveItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var removed = await host.Client.SendAsync(host.WithToken(HttpMethod.Delete, "/api/v1/companion/device", token), ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.NotNull(Assert.Single(await host.Devices.ListDevicesAsync(ct)).RevokedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(host, token, ct));

        // Asked again, with a token that no longer works: the same 401 as any refused token.
        var again = await host.Client.SendAsync(host.WithToken(HttpMethod.Delete, "/api/v1/companion/device", token), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }
}
