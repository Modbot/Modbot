using System.Net;
using System.Net.Http.Json;
using Modbot.Api.Features.Client.Events;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Client;

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

    private static ClientEventDto Event(
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

    private static EventBatchDto Batch(params ClientEventDto[] events)
        => new(Guid.NewGuid().ToString("n"), "2026.9.0", -412, "good", events);

    private async Task<(ClientApiTestHost Host, string Token)> ReadyAsync(CancellationToken ct)
    {
        await ClientApiTestHost.ResetAsync(_db, ct);
        var host = await ClientApiTestHost.StartAsync(_db);
        await host.ConfigureGroupAsync(_db, Group, ct);

        return (host, await host.PairDeviceAsync("Rin's desktop", ct));
    }

    private static async Task<EventBatchResponse> PostAsync(
        ClientApiTestHost host, string? token, EventBatchDto batch, CancellationToken ct)
    {
        var request = host.WithToken(HttpMethod.Post, "/api/v1/client/events", token);
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
            var token = await host.PairDeviceAsync($"moderator {moderator}", ct);

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
        // Recorded as a join, one moderator walking into a room would become forty arrivals, and
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

        var smuggled = new ClientEventDto(
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

        var request = host.WithToken(HttpMethod.Post, "/api/v1/client/events", token);
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

        var request = host.WithToken(HttpMethod.Post, "/api/v1/client/events", token);
        request.Content = JsonContent.Create(Batch(Event(subject: "usr_after")));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public async Task AnEmptyBatchIs400SoTheClientDropsItRatherThanRetryingForever()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/client/events", token);
        request.Content = JsonContent.Create(Batch());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public async Task AnOversizedBatchIs413SoTheClientHalvesAndRetries()
    {
        // Not a 400: nothing is wrong with the events, and dropping them would lose presence that
        // cannot be backfilled.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var request = host.WithToken(HttpMethod.Post, "/api/v1/client/events", token);
        request.Content = JsonContent.Create(Batch(
            [.. Enumerable.Range(0, EventsHandler.MaxEventsPerBatch + 1).Select(i => Event(subject: $"usr_{i}"))]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.SendAsync(request, ct)).StatusCode);
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
}
