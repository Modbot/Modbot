using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Companion.Events;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The possible crash alert end to end: leaves reported by a companion sound one card for the
/// moderators in that instance, naming who joined or changed avatar just before, and the Discord
/// post follows the Flagged setting.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PossibleCrashAlertTests
{
    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private const string Channel = "1234567890";

    private static readonly DateTimeOffset Noon = CompanionApiTestHost.Now;

    private readonly PostgresFixture _db;

    public PossibleCrashAlertTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingPoster : IDiscordChannelPoster
    {
        private readonly List<string> _posts = [];

        public IReadOnlyList<string> Posts
        {
            get
            {
                lock (_posts)
                    return [.. _posts];
            }
        }

        public Task<bool> PostAsync(string channelId, string text, CancellationToken ct = default)
        {
            lock (_posts)
                _posts.Add(text);

            return Task.FromResult(true);
        }
    }

    private async Task<(CompanionApiTestHost Host, string Reporter, string Watcher, RecordingPoster Poster)> ReadyAsync(FlagRuleSettings? rules = null)
    {
        var poster = new RecordingPoster();

        await CompanionApiTestHost.ResetAsync(_db, Ct);
        var host = await CompanionApiTestHost.StartAsync(_db, s => s.AddSingleton<IDiscordChannelPoster>(poster));
        await host.ConfigureGroupAsync(_db, Group, Ct);

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.FlagRules = (rules ?? FlagRuleSettings.Default).ToJson();
            await context.SaveChangesAsync(Ct);
        }

        var reporter = await host.PairDeviceAsync(Ct);
        var watcher = await host.PairDeviceAsync(Ct);

        // The watcher says it is standing in the instance the way an overlay does, by reading its roster.
        (await host.Client.SendAsync(
            host.WithToken(HttpMethod.Get, $"/api/v1/companion/context?instanceId={Instance}", watcher), Ct))
            .EnsureSuccessStatusCode();

        return (host, reporter, watcher, poster);
    }

    private static CompanionEventDto Event(string type, string subject, DateTimeOffset at, string name = "Rin", string? avatar = null)
    {
        var data = new Dictionary<string, string> { ["displayName"] = name };
        if (avatar is not null)
            data["avatarName"] = avatar;

        return new CompanionEventDto(
            Guid.NewGuid().ToString("n"), type, at, null, subject, "wrld_4b34", Instance, Group, data);
    }

    private static async Task ReportAsync(CompanionApiTestHost host, string token, params CompanionEventDto[] events)
    {
        var report = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        report.Content = JsonContent.Create(new EventBatchDto(Guid.NewGuid().ToString("n"), "2026.9.0", 0, "good", events));

        (await host.Client.SendAsync(report, Ct)).EnsureSuccessStatusCode();
    }

    private static CompanionEventDto[] Leaves(int count, DateTimeOffset first)
        => Enumerable.Range(0, count)
            .Select(i => Event("InstanceLeft", $"usr_leaver{i}", first.AddMilliseconds(i * 500)))
            .ToArray();

    private static Task<HttpResponseMessage> PollAsync(CompanionApiTestHost host, string token)
        => host.Client.SendAsync(host.WithToken(HttpMethod.Get, "/api/v1/companion/alerts?wait=1", token), Ct);

    private static async Task AssertQuietAsync(CompanionApiTestHost host, string token)
        => Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, token)).StatusCode);

    [Fact]
    public async Task EnoughLeavesInTheWindow_AlertTheModeratorsInThatInstance_NamingWhoWasJustBefore()
    {
        var (host, reporter, watcher, _) = await ReadyAsync();
        await using var _ = host;

        await ReportAsync(
            host,
            reporter,
            Event("InstanceJoined", "usr_joiner", Noon.AddSeconds(-6), "Joiner"),
            Event("AvatarChanged", "usr_changer", Noon.AddSeconds(-2), "Changer", "Loud Fox"));
        await AssertQuietAsync(host, watcher);

        await ReportAsync(host, reporter, Leaves(5, Noon));

        var response = await PollAsync(host, watcher);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alert = (await response.Content.ReadFromJsonAsync<FlaggedJoinAlertDto>(Ct))!;
        Assert.Equal(AlertHub.CrashKind, alert.Kind);
        Assert.Equal(Instance, alert.InstanceId);
        Assert.Equal("wrld_4b34", alert.WorldId);
        Assert.Equal("5 people left within 5 seconds", alert.Reason);
        Assert.Equal(["usr_changer", "usr_joiner"], alert.People!.Select(p => p.SubjectId));
        Assert.Equal(["Changer", "Joiner"], alert.People!.Select(p => p.DisplayName));
    }

    [Fact]
    public async Task OneFewerThanTheCount_AlertsNobody()
    {
        var (host, reporter, watcher, poster) = await ReadyAsync(new FlagRuleSettings { DiscordPost = true, DiscordChannelId = Channel });
        await using var _ = host;

        await ReportAsync(host, reporter, Leaves(4, Noon));

        await AssertQuietAsync(host, watcher);
        Assert.Empty(poster.Posts);
    }

    [Fact]
    public async Task TheCountAndTheWindowAreTheSettings()
    {
        var (host, reporter, watcher, _) = await ReadyAsync(new FlagRuleSettings { CrashLeaves = 3, CrashSeconds = 2 });
        await using var _ = host;

        await ReportAsync(host, reporter, Leaves(3, Noon));

        Assert.Equal(HttpStatusCode.OK, (await PollAsync(host, watcher)).StatusCode);
    }

    [Fact]
    public async Task TurnedOff_NothingIsRaised()
    {
        var (host, reporter, watcher, _) = await ReadyAsync(new FlagRuleSettings { CrashAlert = false });
        await using var _ = host;

        await ReportAsync(host, reporter, Leaves(8, Noon));

        await AssertQuietAsync(host, watcher);
    }

    [Fact]
    public async Task AnotherCompanionReportingTheSameCrash_DoesNotRaiseItAgain()
    {
        var (host, reporter, watcher, _) = await ReadyAsync();
        await using var _ = host;

        await ReportAsync(host, reporter, Leaves(5, Noon));
        Assert.Equal(HttpStatusCode.OK, (await PollAsync(host, watcher)).StatusCode);

        await ReportAsync(host, watcher, Leaves(5, Noon.AddMilliseconds(30)));

        await AssertQuietAsync(host, watcher);
    }

    [Fact]
    public async Task WithThePostSettingOn_OneLineGoesToDiscord_NamingTheCause()
    {
        var (host, reporter, watcher, poster) = await ReadyAsync(new FlagRuleSettings { DiscordPost = true, DiscordChannelId = Channel });
        await using var _ = host;

        await ReportAsync(host, reporter, Event("AvatarChanged", "usr_changer", Noon.AddSeconds(-2), "Changer", "Loud Fox"));
        await ReportAsync(host, reporter, Leaves(5, Noon));

        var post = Assert.Single(poster.Posts);
        Assert.Contains("Possible crash", post, StringComparison.Ordinal);
        Assert.Contains("5 people", post, StringComparison.Ordinal);
        Assert.Contains("Changer", post, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await PollAsync(host, watcher)).StatusCode);
    }
}
