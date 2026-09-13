using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Client.Alerts;
using Modbot.Api.Features.Client.Context;
using Modbot.Api.Features.Client.Events;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Modbot.Api.Tests.Features.Client;

/// <summary>
/// What the overlay reads, and the one thing it is pushed.
/// </summary>
/// <remarks>
/// These fill a cache the overlay renders from; it never calls them while drawing. So the property
/// that matters here is not latency under load — it is that the answers are small, complete, and
/// derived from this deployment's own fact log rather than from a VRChat call a moderator's glance
/// would be spending the group's shared API budget on.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class OverlayReadTests
{
    private readonly PostgresFixture _db;

    public OverlayReadTests(PostgresFixture db) => _db = db;

    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private async Task<(ClientApiTestHost Host, string Token)> ReadyAsync(CancellationToken ct)
    {
        await ClientApiTestHost.ResetAsync(_db, ct);
        var host = await ClientApiTestHost.StartAsync(_db);
        await host.ConfigureGroupAsync(_db, Group, ct);

        return (host, await host.PairDeviceAsync(ct));
    }

    private static async Task WriteAsync(ClientApiTestHost host, params FactRecord[] facts)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteManyAsync(facts);
    }

    private static FactRecord Fact(
        string type,
        string subject,
        DateTimeOffset at,
        string? displayName = null,
        string? instance = Instance)
        => new()
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = "wrld_4b34",
            InstanceId = instance,
            Source = FactSource.Modbot,
            Data = displayName is null
                ? null
                : new System.Text.Json.Nodes.JsonObject { ["displayName"] = displayName },
        };

    private static async Task<T> GetAsync<T>(
        ClientApiTestHost host, string token, string path, CancellationToken ct)
    {
        var response = await host.Client.SendAsync(host.WithToken(HttpMethod.Get, path, token), ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    /// <summary>
    /// Puts a device in an instance, the way a running overlay does.
    /// </summary>
    /// <remarks>
    /// Alerts are scoped to the instance a device is standing in, and a device says where that is
    /// by reading that instance's roster — which an overlay does every twenty seconds whether or
    /// not anything is happening. Nothing extra goes on the wire to establish it, which is exactly
    /// why this is a roster read and not a new call.
    /// </remarks>
    private static async Task StandingInAsync(
        ClientApiTestHost host, string token, string instance, CancellationToken ct)
    {
        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v1/client/context?instanceId={instance}", token), ct);

        response.EnsureSuccessStatusCode();
    }

    private static async Task ReportJoinAsync(
        ClientApiTestHost host,
        string token,
        string subject,
        CancellationToken ct,
        string instance = Instance,
        string? displayName = null)
    {
        var report = host.WithToken(HttpMethod.Post, "/api/v1/client/events", token);
        report.Content = JsonContent.Create(new EventBatchDto(
            Guid.NewGuid().ToString("n"), "2026.9.0", 0, "good",
            [
                new ClientEventDto(
                    Guid.NewGuid().ToString("n"), "InstanceJoined", Noon, null, subject,
                    "wrld_4b34", instance, Group,
                    displayName is null ? null : new Dictionary<string, string> { ["displayName"] = displayName }),
            ]));

        var response = await host.Client.SendAsync(report, ct);
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> PollAsync(
        ClientApiTestHost host, string token, CancellationToken ct, int wait = 1)
        => host.Client.SendAsync(host.WithToken(HttpMethod.Get, $"/api/v1/client/alerts?wait={wait}", token), ct);

    [Fact]
    public async Task ARosterIsWhoIsStillHere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await WriteAsync(host,
            Fact(FactType.InstanceJoined, "usr_stay", Noon.AddMinutes(-30), "Staying"),
            Fact(FactType.InstanceJoined, "usr_gone", Noon.AddMinutes(-25), "Leaving"),
            Fact(FactType.InstanceLeft, "usr_gone", Noon.AddMinutes(-5), "Leaving"));

        var roster = await GetAsync<InstanceContextDto>(
            host, token, $"/api/v1/client/context?instanceId={Instance}", ct);

        var member = Assert.Single(roster.Members);
        Assert.Equal("usr_stay", member.SubjectId);
        Assert.Equal("Staying", member.DisplayName);
    }

    [Fact]
    public async Task SomebodyWhoLeftAndCameBackIsInTheRoster()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await WriteAsync(host,
            Fact(FactType.InstanceJoined, "usr_back", Noon.AddMinutes(-30)),
            Fact(FactType.InstanceLeft, "usr_back", Noon.AddMinutes(-20)),
            Fact(FactType.InstanceJoined, "usr_back", Noon.AddMinutes(-10)));

        var roster = await GetAsync<InstanceContextDto>(
            host, token, $"/api/v1/client/context?instanceId={Instance}", ct);

        Assert.Equal("usr_back", Assert.Single(roster.Members).SubjectId);
    }

    [Fact]
    public async Task SomebodyWithPriorActionsIsFlagged()
    {
        // The highest-value thing the overlay shows, and the reason the roster carries standing
        // rather than just names.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await WriteAsync(host,
            Fact(FactType.MemberKicked, "usr_flag", Noon.AddDays(-40), instance: null),
            Fact(FactType.MemberKicked, "usr_flag", Noon.AddDays(-20), instance: null),
            Fact(FactType.InstanceJoined, "usr_flag", Noon.AddMinutes(-5), "Trouble"));

        var roster = await GetAsync<InstanceContextDto>(
            host, token, $"/api/v1/client/context?instanceId={Instance}", ct);

        var member = Assert.Single(roster.Members);
        Assert.Equal("Flagged", member.Standing);
        Assert.Equal(2, member.PriorActions);
        Assert.Equal("2 prior actions", Assert.Single(member.Flags));
    }

    [Fact]
    public async Task AnInstanceNobodyHasReportedIsAnEmptyRosterRatherThanAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var roster = await GetAsync<InstanceContextDto>(
            host, token, "/api/v1/client/context?instanceId=nobody-here", ct);

        Assert.Empty(roster.Members);
    }

    [Fact]
    public async Task SomebodyWithNothingOnRecordIsASuccessfulEmptyAnswerRatherThanA404()
    {
        // The overlay would have to translate a 404 into "nothing known", and a card that says
        // "not found" reads as a fault rather than as the ordinary answer it is.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var summary = await GetAsync<UserSummaryDto>(host, token, "/api/v1/client/user/usr_nobody", ct);

        Assert.Equal("usr_nobody", summary.SubjectId);
        Assert.Equal(0, summary.PriorActions);
        Assert.Empty(summary.Flags);
    }

    [Fact]
    public async Task AProfileSummaryCarriesWhatFitsOnAHeadsetCard()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        await WriteAsync(host,
            Fact(FactType.MemberJoined, "usr_sub", Noon.AddDays(-25), "Rin", instance: null),
            Fact(FactType.MemberKicked, "usr_sub", Noon.AddDays(-10), instance: null));

        var summary = await GetAsync<UserSummaryDto>(host, token, "/api/v1/client/user/usr_sub", ct);

        Assert.Equal("Flagged", summary.Standing);
        Assert.Equal(1, summary.PriorActions);
        Assert.Equal(Noon.AddDays(-25), summary.JoinedAt);
        Assert.Equal("Rin", summary.DisplayName);
        Assert.Equal("1 prior action", Assert.Single(summary.Flags));
    }

    [Fact]
    public async Task TheReadsRefuseAnUnknownToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, _) = await ReadyAsync(ct);
        await using var __ = host;

        foreach (var path in (string[])
            [$"/api/v1/client/context?instanceId={Instance}", "/api/v1/client/user/usr_x", "/api/v1/client/alerts?wait=1"])
        {
            var response = await host.Client.SendAsync(host.WithToken(HttpMethod.Get, path, "nope"), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task AQuietLongPollAnswers204AfterWaitingRatherThanReturningAtOnce()
    {
        // 204 rather than an empty 200, so a quiet evening cannot look like an alert whose body
        // failed to parse. And it really waits: a long poll that returned immediately would be an
        // ordinary poll wearing a costume.
        var ct = TestContext.Current.CancellationToken;
        var (host, token) = await ReadyAsync(ct);
        await using var _ = host;

        var stopwatch = Stopwatch.StartNew();
        var response = await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, "/api/v1/client/alerts?wait=1", token), ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800), $"returned after {stopwatch.Elapsed}");
    }


    [Fact]
    public async Task AFlaggedUserJoiningWakesTheOtherModeratorsInThatInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var watcher = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, watcher, Instance, ct);

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));

        // The watcher is already waiting when the join is reported, which is the real shape of it.
        var waiting = PollAsync(host, watcher, ct, wait: 20);

        await ReportJoinAsync(host, reporter, "usr_flag", ct, displayName: "Trouble");

        var response = await waiting;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alert = await response.Content.ReadFromJsonAsync<FlaggedJoinAlertDto>(ct);
        Assert.Equal("usr_flag", alert!.SubjectId);
        Assert.Equal("Trouble", alert.DisplayName);
        Assert.Equal(1, alert.PriorActions);
    }

    [Fact]
    public async Task AModeratorInADifferentInstanceIsNotToldAboutIt()
    {
        // The scoping rule, and it is a data-minimisation one before it is a bandwidth one: a
        // moderator standing somewhere else cannot act on the card, and telling them anyway hands
        // their machine a colleague's instance and the name of somebody who just walked into it.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var elsewhere = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, elsewhere, "77777", ct);

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));
        await ReportJoinAsync(host, reporter, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, elsewhere, ct)).StatusCode);
    }

    [Fact]
    public async Task ADeviceThatHasNotSaidWhereItIsIsToldNothing()
    {
        // Fail closed. A device that has not named an instance is one whose VRChat is closed, or
        // whose client is paused, or which has only just paired -- none of which wants a card, and
        // all of which would otherwise receive every alert this deployment raises.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var idle = await host.PairDeviceAsync(ct);

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));
        await ReportJoinAsync(host, reporter, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, idle, ct)).StatusCode);
    }

    [Fact]
    public async Task ReportingPresenceIsEnoughToBePlacedInAnInstance()
    {
        // The other way a device says where it is, and the one that needs no overlay running: the
        // ingest batch already names the instance its observations came from. Neither route adds a
        // byte to the wire, which is what keeps this from becoming a second presence channel.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var watcher = await host.PairDeviceAsync(ct);
        await ReportJoinAsync(host, watcher, "usr_ordinary", ct);

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));
        await ReportJoinAsync(host, reporter, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.OK, (await PollAsync(host, watcher, ct)).StatusCode);
    }

    [Fact]
    public async Task WalkingIntoAnotherInstanceMovesWhichAlertsAModeratorGets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var wanderer = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, wanderer, Instance, ct);
        await StandingInAsync(host, wanderer, "77777", ct);

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));
        await ReportJoinAsync(host, reporter, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, wanderer, ct)).StatusCode);
    }

    [Fact]
    public async Task ADeviceThatStoppedReportingHoursAgoIsNoLongerCreditedWithAnInstance()
    {
        // The ordinary way a session ends is that it stops: VRChat is closed, crashes, or the
        // machine sleeps, and no client sends a goodbye. A location that never expired would have
        // this deployment broadcasting to moderators who went to bed last night.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var gone = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, gone, Instance, ct);

        host.Clock.Advance(DeviceLocations.RememberedFor + TimeSpan.FromMinutes(1));

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));
        await ReportJoinAsync(host, reporter, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, gone, ct)).StatusCode);
    }

    [Fact]
    public async Task TheClientThatReportedTheJoinIsNotAlertedAboutIt()
    {
        // It read the arrival out of its own log a moment ago. Telling it again would put a card
        // in front of the one moderator who does not need one.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        await StandingInAsync(host, reporter, Instance, ct);
        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));
        await ReportJoinAsync(host, reporter, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, reporter, ct)).StatusCode);
    }

    [Fact]
    public async Task AnOrdinaryJoinRaisesNothing()
    {
        // An overlay that interrupts constantly gets disabled, and a disabled overlay notifies
        // nobody.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var watcher = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, watcher, Instance, ct);

        await ReportJoinAsync(host, reporter, "usr_ordinary", ct);

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, watcher, ct)).StatusCode);
    }

    [Fact]
    public async Task PresenceObservedRaisesNothingEvenForAFlaggedUser()
    {
        // Somebody already in the room when a moderator walked in is not an arrival, so alerting
        // on it would fire a card for the whole room every time anybody entered -- the phantom
        // burst problem, one layer up.
        var ct = TestContext.Current.CancellationToken;
        var (host, reporter) = await ReadyAsync(ct);
        await using var _ = host;

        var watcher = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, watcher, Instance, ct);
        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));

        var report = host.WithToken(HttpMethod.Post, "/api/v1/client/events", reporter);
        report.Content = JsonContent.Create(new EventBatchDto(
            "batch", "2026.9.0", 0, "good",
            [
                new ClientEventDto(
                    "e1", "InstancePresenceObserved", Noon, null, "usr_flag", "wrld_4b34", Instance, Group, null),
            ]));

        (await host.Client.SendAsync(report, ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, watcher, ct)).StatusCode);
    }

    [Fact]
    public async Task ASecondReportOfTheSameJoinDoesNotAlertAgain()
    {
        // Six moderators reporting one arrival must interrupt once, not six times.
        var ct = TestContext.Current.CancellationToken;
        var (host, first) = await ReadyAsync(ct);
        await using var _ = host;

        var second = await host.PairDeviceAsync(ct);
        var watcher = await host.PairDeviceAsync(ct);
        await StandingInAsync(host, watcher, Instance, ct);

        await WriteAsync(host, Fact(FactType.MemberBanned, "usr_flag", Noon.AddDays(-10), instance: null));

        foreach (var token in (string[])[first, second])
            await ReportJoinAsync(host, token, "usr_flag", ct);

        Assert.Equal(HttpStatusCode.OK, (await PollAsync(host, watcher, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, watcher, ct)).StatusCode);
    }
}
