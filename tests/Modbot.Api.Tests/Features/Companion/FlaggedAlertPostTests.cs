using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Companion.Events;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Notifications;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The optional Discord post for a flagged join, and which arrivals tell the team.
/// </summary>
/// <remarks>
/// Off to start with, so nothing is posted until the setting is on and a channel is picked. When it
/// is on, every flagged join is posted, not only a watched person's. Somebody already in the instance
/// when a moderator arrived is for the companion's card only: no post, and no notification to the team.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class FlaggedAlertPostTests
{
    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private const string Channel = "1234567890";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public FlaggedAlertPostTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingPoster : IDiscordChannelPoster
    {
        private readonly List<(string Channel, string Text)> _posts = [];

        public IReadOnlyList<(string Channel, string Text)> Posts
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
                _posts.Add((channelId, text));

            return Task.FromResult(true);
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        private readonly List<Notification> _raised = [];

        public IReadOnlyList<Notification> Raised
        {
            get
            {
                lock (_raised)
                    return [.. _raised];
            }
        }

        public Task<NotificationOutcome> RaiseAsync(Notification notification, CancellationToken ct = default)
        {
            lock (_raised)
                _raised.Add(notification);

            return Task.FromResult(NotificationOutcome.Nothing);
        }
    }

    private async Task<(CompanionApiTestHost Host, string Token, RecordingPoster Poster, RecordingNotifier Notifier)> ReadyAsync(
        FlagRuleSettings? rules = null)
    {
        var poster = new RecordingPoster();
        var notifier = new RecordingNotifier();

        await CompanionApiTestHost.ResetAsync(_db, Ct);
        var host = await CompanionApiTestHost.StartAsync(
            _db,
            s =>
            {
                s.AddSingleton<IDiscordChannelPoster>(poster);
                s.AddSingleton<INotifier>(notifier);
            });
        await host.ConfigureGroupAsync(_db, Group, Ct);

        await using (var context = _db.NewContext())
        {
            await context.PersonWatches.ExecuteDeleteAsync(Ct);

            var settings = await context.GetSettingsAsync(Ct);
            settings.FlagRules = (rules ?? FlagRuleSettings.Default).ToJson();
            await context.SaveChangesAsync(Ct);
        }

        return (host, await host.PairDeviceAsync(Ct), poster, notifier);
    }

    private static FlagRuleSettings PostingInChannel => new() { DiscordPost = true, DiscordChannelId = Channel };

    private static async Task BanAsync(CompanionApiTestHost host, string person)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteManyAsync(
        [
            new FactRecord
            {
                Type = FactType.MemberBanned,
                OccurredAt = Noon.AddDays(-10),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = person,
                Source = FactSource.Modbot,
            },
        ]);
    }

    private async Task WatchAsync(string person)
    {
        await using var context = _db.NewContext();
        context.PersonWatches.Add(new PersonWatch
        {
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            Reason = "Said they would come back",
            SetByUserId = Guid.NewGuid(),
            SetByUsername = "mira",
            SetAt = Noon.AddDays(-1),
        });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task ReportAsync(
        CompanionApiTestHost host, string token, string type, string subject, string? displayName = null)
    {
        var report = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        report.Content = JsonContent.Create(new EventBatchDto(
            Guid.NewGuid().ToString("n"), "2026.9.0", 0, "good",
            [
                new CompanionEventDto(
                    Guid.NewGuid().ToString("n"), type, Noon, null, subject,
                    "wrld_4b34", Instance, Group,
                    displayName is null ? null : new Dictionary<string, string> { ["displayName"] = displayName }),
            ]));

        (await host.Client.SendAsync(report, Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ByDefault_AFlaggedJoin_PostsNothing()
    {
        var (host, token, poster, _) = await ReadyAsync();
        await using var _ = host;

        await BanAsync(host, "usr_flag");
        await ReportAsync(host, token, "InstanceJoined", "usr_flag", "Rin");

        Assert.Empty(poster.Posts);
    }

    [Fact]
    public async Task SwitchedOnWithoutAChannel_PostsNothing()
    {
        var (host, token, poster, _) = await ReadyAsync(new FlagRuleSettings { DiscordPost = true });
        await using var _ = host;

        await BanAsync(host, "usr_flag");
        await ReportAsync(host, token, "InstanceJoined", "usr_flag", "Rin");

        Assert.Empty(poster.Posts);
    }

    [Fact]
    public async Task SwitchedOn_EveryFlaggedJoinIsPosted_AndNotOnlyWatchedPeople()
    {
        var (host, token, poster, notifier) = await ReadyAsync(PostingInChannel);
        await using var _ = host;

        // Banned, never watched: the team's notification is for watches, this post is for every flag.
        await BanAsync(host, "usr_flag");
        await ReportAsync(host, token, "InstanceJoined", "usr_flag", "Rin");
        await ReportAsync(host, token, "InstanceJoined", "usr_ordinary", "Calm");

        var post = Assert.Single(poster.Posts);
        Assert.Equal(Channel, post.Channel);
        Assert.Contains("Rin", post.Text);
        Assert.Contains("1 kick or ban", post.Text);
        Assert.DoesNotContain("Calm", post.Text);
        Assert.Empty(notifier.Raised);
    }

    [Fact]
    public async Task TwoModeratorsReportingOneJoin_IsOnePost()
    {
        var (host, first, poster, _) = await ReadyAsync(PostingInChannel);
        await using var _ = host;

        var second = await host.PairDeviceAsync(Ct);
        await BanAsync(host, "usr_flag");

        await ReportAsync(host, first, "InstanceJoined", "usr_flag", "Rin");
        await ReportAsync(host, second, "InstanceJoined", "usr_flag", "Rin");

        Assert.Single(poster.Posts);
    }

    [Fact]
    public async Task AFlaggedPersonAlreadyThere_IsNotPosted()
    {
        var (host, token, poster, _) = await ReadyAsync(PostingInChannel);
        await using var _ = host;

        await BanAsync(host, "usr_flag");
        await ReportAsync(host, token, "InstancePresenceObserved", "usr_flag", "Rin");

        Assert.Empty(poster.Posts);
    }

    [Fact]
    public async Task AWatchedPersonAlreadyThere_SendsNoNotificationToTheTeam()
    {
        var (host, token, _, notifier) = await ReadyAsync();
        await using var _ = host;

        await WatchAsync("usr_watched");
        await ReportAsync(host, token, "InstancePresenceObserved", "usr_watched", "Quinn");

        Assert.Empty(notifier.Raised);
    }

    [Fact]
    public async Task AWatchedPersonWhoJoins_StillSendsTheNotification()
    {
        var (host, token, _, notifier) = await ReadyAsync();
        await using var _ = host;

        await WatchAsync("usr_watched");
        await ReportAsync(host, token, "InstanceJoined", "usr_watched", "Quinn");

        Assert.Equal(NotificationKinds.WatchedPersonJoined, Assert.Single(notifier.Raised).Kind);
    }

    [Fact]
    public async Task ThePostHelper_SaysNothing_WhenOffOrWithoutAChannel()
    {
        var poster = new RecordingPoster();

        Assert.False(await FlaggedDiscordPost.PostAsync(poster, new FlagRuleSettings(), "x", Ct));
        Assert.False(await FlaggedDiscordPost.PostAsync(poster, new FlagRuleSettings { DiscordPost = true }, "x", Ct));
        Assert.Empty(poster.Posts);

        Assert.True(await FlaggedDiscordPost.PostAsync(poster, PostingInChannel, "x", Ct));
        Assert.Equal((Channel, "x"), Assert.Single(poster.Posts));
    }
}
