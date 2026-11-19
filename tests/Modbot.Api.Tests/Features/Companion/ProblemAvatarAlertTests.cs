using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Companion.Events;
using Modbot.Api.Features.ProblemAvatars;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The problem avatar list's alert: somebody who joins in, is found in, or switches to a listed
/// avatar sounds the flagged card for the moderators in that instance, once.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ProblemAvatarAlertTests
{
    private const string Group = "grp_cats";

    private const string Instance = "39911";

    private const string Channel = "1234567890";

    private const string Tiger = "White Tiger";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public ProblemAvatarAlertTests(PostgresFixture db) => _db = db;

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

    /// <param name="listed">
    /// The names the list holds. Null leaves the table as it is, for a test that filled it some
    /// other way first.
    /// </param>
    private async Task<(CompanionApiTestHost Host, string Reporter, string Watcher, RecordingPoster Poster)> ReadyAsync(
        string[]? listed = null, FlagRuleSettings? rules = null)
    {
        var poster = new RecordingPoster();

        await CompanionApiTestHost.ResetAsync(_db, Ct, keepProblemAvatars: listed is null);
        var host = await CompanionApiTestHost.StartAsync(_db, s => s.AddSingleton<IDiscordChannelPoster>(poster));
        await host.ConfigureGroupAsync(_db, Group, Ct);

        await using (var context = _db.NewContext())
        {
            if (listed is not null)
            {
                await context.ProblemAvatars.ExecuteDeleteAsync(Ct);

                foreach (var name in listed)
                {
                    context.ProblemAvatars.Add(new ProblemAvatar
                    {
                        Name = name,
                        NameKey = ProblemAvatar.KeyOf(name),
                        AddedByUserId = Guid.NewGuid(),
                        AddedByUsername = "mira",
                        AddedAt = Noon.AddDays(-1),
                    });
                }
            }

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

    private static async Task ReportAsync(
        CompanionApiTestHost host,
        string token,
        string type,
        string subject,
        string? avatar,
        DateTimeOffset? at = null,
        string displayName = "Rin",
        string instance = Instance)
    {
        var data = new Dictionary<string, string> { ["displayName"] = displayName };
        if (avatar is not null)
            data["avatarName"] = avatar;

        var report = host.WithToken(HttpMethod.Post, "/api/v1/companion/events", token);
        report.Content = JsonContent.Create(new EventBatchDto(
            Guid.NewGuid().ToString("n"), "2026.9.0", 0, "good",
            [
                new CompanionEventDto(
                    Guid.NewGuid().ToString("n"), type, at ?? Noon, null, subject,
                    "wrld_4b34", instance, Group, data),
            ]));

        (await host.Client.SendAsync(report, Ct)).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> PollAsync(CompanionApiTestHost host, string token)
        => host.Client.SendAsync(host.WithToken(HttpMethod.Get, "/api/v1/companion/alerts?wait=1", token), Ct);

    private static async Task<FlaggedJoinAlertDto> AlertAsync(CompanionApiTestHost host, string token)
    {
        var response = await PollAsync(host, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<FlaggedJoinAlertDto>(Ct))!;
    }

    private static async Task AssertQuietAsync(CompanionApiTestHost host, string token)
        => Assert.Equal(HttpStatusCode.NoContent, (await PollAsync(host, token)).StatusCode);

    [Fact]
    public async Task AJoinWearingAListedAvatar_AlertsTheModeratorsInThatInstance_NamingTheAvatar()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "InstanceJoined", "usr_wearer", Tiger);

        var alert = await AlertAsync(host, watcher);
        Assert.Equal("usr_wearer", alert.SubjectId);
        Assert.Equal("Rin", alert.DisplayName);
        Assert.Equal(Instance, alert.InstanceId);
        Assert.Equal("wrld_4b34", alert.WorldId);
        Assert.Equal($"Wearing a problem avatar: {Tiger}", alert.Reason);
        Assert.Equal(0, alert.PriorActions);
    }

    [Fact]
    public async Task SwitchingToAListedAvatarMidInstance_Alerts()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", "Plain Fox", Noon.AddMinutes(-3));
        await AssertQuietAsync(host, watcher);

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger, Noon);

        Assert.Equal("usr_wearer", (await AlertAsync(host, watcher)).SubjectId);
    }

    [Fact]
    public async Task AnUnlistedAvatar_AlertsNobody_AndNeitherDoesAnEmptyList()
    {
        var (host, reporter, watcher, poster) = await ReadyAsync([Tiger], PostingInChannel);
        await using var _ = host;

        await ReportAsync(host, reporter, "InstanceJoined", "usr_calm", "Plain Fox");
        await ReportAsync(host, reporter, "InstanceJoined", "usr_bare", avatar: null);

        await AssertQuietAsync(host, watcher);
        Assert.Empty(poster.Posts);

        await using (var context = _db.NewContext())
            await context.ProblemAvatars.ExecuteDeleteAsync(Ct);

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger);

        await AssertQuietAsync(host, watcher);
    }

    [Fact]
    public async Task TheNameIsMatchedWithoutRegardToCaseOrSurroundingSpaces()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", "  white TIGER ");

        Assert.Equal("usr_wearer", (await AlertAsync(host, watcher)).SubjectId);
    }

    [Fact]
    public async Task OnlyTheWholeName_Matches_NotPartOfIt()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_cub", "White Tiger Cub");
        await ReportAsync(host, reporter, "AvatarChanged", "usr_part", "White");

        await AssertQuietAsync(host, watcher);
    }

    [Fact]
    public async Task AnAvatarAddedFromALink_AlertsByTheNameVRChatGave()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([]);
        await using var _ = host;

        await using (var context = _db.NewContext())
        {
            context.ProblemAvatars.Add(new ProblemAvatar
            {
                Name = Tiger,
                NameKey = ProblemAvatar.KeyOf(Tiger),
                AvatarId = "avtr_4f2f3a52-0000-0000-0000-000000000001",
                AddedByUserId = Guid.NewGuid(),
                AddedByUsername = "mira",
                AddedAt = Noon.AddDays(-1),
            });
            await context.SaveChangesAsync(Ct);
        }

        await ReportAsync(host, reporter, "InstanceJoined", "usr_wearer", Tiger);

        Assert.Equal("usr_wearer", (await AlertAsync(host, watcher)).SubjectId);
    }

    [Fact]
    public async Task AnAvatarAddedByIdWithNoName_AlertsNobody_NotEvenSomeoneWithABlankAvatarName()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([]);
        await using var _ = host;

        await using (var context = _db.NewContext())
        {
            context.ProblemAvatars.Add(new ProblemAvatar
            {
                AvatarId = "avtr_4f2f3a52-0000-0000-0000-000000000002",
                AddedByUserId = Guid.NewGuid(),
                AddedByUsername = "mira",
                AddedAt = Noon.AddDays(-1),
            });
            await context.SaveChangesAsync(Ct);
        }

        await ReportAsync(host, reporter, "InstanceJoined", "usr_blank", " ");
        await ReportAsync(host, reporter, "InstanceJoined", "usr_wearer", Tiger);

        await AssertQuietAsync(host, watcher);
    }

    [Fact]
    public async Task AModeratorWhoFoundSomeoneAlreadyThereWearingOne_IsToldOfIt()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "InstancePresenceObserved", "usr_wearer", Tiger);

        Assert.Equal("usr_wearer", (await AlertAsync(host, watcher)).SubjectId);
    }

    [Fact]
    public async Task TheModeratorWhoReportedIt_IsToldToo()
    {
        var (host, reporter, _, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger);

        Assert.Equal("usr_wearer", (await AlertAsync(host, reporter)).SubjectId);
    }

    [Fact]
    public async Task OnePersonInOneAvatarInOneInstance_AlertsOnce_WhoeverReportsThemAgain()
    {
        var (host, reporter, watcher, poster) = await ReadyAsync([Tiger], PostingInChannel);
        await using var _ = host;

        await ReportAsync(host, reporter, "InstancePresenceObserved", "usr_wearer", Tiger, Noon.AddMinutes(-30));
        await AlertAsync(host, watcher);
        await AlertAsync(host, reporter);

        // Another moderator walks in later and finds the same person in the same avatar.
        await ReportAsync(host, watcher, "InstancePresenceObserved", "usr_wearer", Tiger, Noon);

        await AssertQuietAsync(host, watcher);
        await AssertQuietAsync(host, reporter);
        Assert.Single(poster.Posts);
    }

    [Fact]
    public async Task AModeratorInADifferentInstance_IsNotTold()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger, instance: "77777");

        await AssertQuietAsync(host, watcher);
    }

    [Fact]
    public async Task AnAvatarTakenOffTheList_StopsAlerting()
    {
        var (host, reporter, watcher, _) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await using (var context = _db.NewContext())
            await context.ProblemAvatars.ExecuteDeleteAsync(Ct);

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger);

        await AssertQuietAsync(host, watcher);
    }

    private static FlagRuleSettings PostingInChannel => new() { DiscordPost = true, DiscordChannelId = Channel };

    [Fact]
    public async Task WithTheDiscordPostOn_AProblemAvatarIsPostedOnce_NamingThePersonAndTheAvatar()
    {
        var (host, reporter, _, poster) = await ReadyAsync([Tiger], PostingInChannel);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger);

        var post = Assert.Single(poster.Posts);
        Assert.Equal(Channel, post.Channel);
        Assert.Contains("Rin", post.Text);
        Assert.Contains(Tiger, post.Text);
    }

    [Fact]
    public async Task WithTheDiscordPostOff_NothingIsPosted_ButTheCardStillGoesOut()
    {
        var (host, reporter, watcher, poster) = await ReadyAsync([Tiger]);
        await using var _ = host;

        await ReportAsync(host, reporter, "AvatarChanged", "usr_wearer", Tiger);

        await AlertAsync(host, watcher);
        Assert.Empty(poster.Posts);
    }

    /// <summary>
    /// The two halves together: a moderator marks the avatar a person was seen wearing from the
    /// person's page, and the next person to join wearing it is alerted on.
    /// </summary>
    [Fact]
    public async Task MarkingAPersonsAvatar_ThenALaterJoinWearingIt_Alerts()
    {
        string stored;

        await using (var surface = await ReadSurfaceTestHost.StartAsync(_db))
        {
            await surface.ResetAsync(Ct);

            await surface.WriteFactAsync(
                new FactRecord
                {
                    Type = FactType.AvatarChanged,
                    OccurredAt = Noon.AddMinutes(-20),
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = "usr_first",
                    WorldId = "wrld_4b34",
                    InstanceId = Instance,
                    Source = FactSource.Modbot,
                    Data = new JsonObject { ["avatarName"] = Tiger },
                },
                Ct);

            var cookie = await surface.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
            var response = await surface.PostJsonAsync(
                "/api/problem-avatars/from-person", new { vrchatUserId = "usr_first" }, cookie, Ct);

            response.EnsureSuccessStatusCode();
            stored = (await response.Content.ReadFromJsonAsync<ProblemAvatarView>(Ct))!.Name;
        }

        Assert.Equal(Tiger, stored);

        var (host, reporter, watcher, _) = await ReadyAsync();
        await using var _ = host;

        await ReportAsync(host, reporter, "InstanceJoined", "usr_second", Tiger, displayName: "Calm");

        var alert = await AlertAsync(host, watcher);
        Assert.Equal("usr_second", alert.SubjectId);
        Assert.Equal($"Wearing a problem avatar: {Tiger}", alert.Reason);
    }

    [Fact]
    public void TheSeenRecord_RemembersAPairForTwelveHours_AndThenForgetsIt()
    {
        var seen = new ProblemAvatarSeen();

        Assert.True(seen.TryMark("usr_a", "white tiger", "wrld_1", "1", Noon));
        Assert.False(seen.TryMark("usr_a", "white tiger", "wrld_1", "1", Noon.AddHours(11)));

        // A different person, avatar, world or number is a different pair.
        Assert.True(seen.TryMark("usr_b", "white tiger", "wrld_1", "1", Noon));
        Assert.True(seen.TryMark("usr_a", "fox", "wrld_1", "1", Noon));
        Assert.True(seen.TryMark("usr_a", "white tiger", "wrld_2", "1", Noon));
        Assert.True(seen.TryMark("usr_a", "white tiger", "wrld_1", "2", Noon));

        Assert.True(seen.TryMark("usr_a", "white tiger", "wrld_1", "1", Noon.AddHours(13)));
    }
}
