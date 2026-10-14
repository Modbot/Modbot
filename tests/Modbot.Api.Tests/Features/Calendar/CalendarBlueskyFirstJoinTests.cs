using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The "We're live" reply under a date's Bluesky post (calendar design, Bluesky extras): made once,
/// when the first person is in the instance, under that date's post, with the join link when anyone
/// may join and the group's page otherwise; nothing without a post for the date.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CalendarBlueskyFirstJoinTests(PostgresFixture db)
{
    private const string World = "wrld_cabin";
    private const string Group = "grp_ourgroup";
    private const string Location = $"{World}:12345~group({Group})~groupAccessType(plus)~region(us)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

    private sealed record Scene(Guid EventId, DateTimeOffset StartsAt, Guid InstanceId, Guid ParentId);

    /// <summary>A checked Bluesky account with Posting on, and no events or posts.</summary>
    private async Task SetUpAsync(Action<Modbot.Core.Data.Entities.Settings>? change = null)
    {
        await using var context = db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);
        await context.CalendarOpenings.ExecuteDeleteAsync(Ct);
        await context.CalendarDateChanges.ExecuteDeleteAsync(Ct);
        await context.CalendarEventPlaces.ExecuteDeleteAsync(Ct);
        await context.CalendarEvents.IgnoreAutoIncludes().ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PostsPaused = false;
        settings.BlueskyHandle = FakeBluesky.Handle;
        settings.BlueskyDid = FakeBluesky.Did;
        settings.BlueskyServer = FakeBluesky.Server;
        settings.BlueskyAppPasswordEncrypted = "sealed";
        settings.BlueskyCheckedAt = DateTimeOffset.UnixEpoch;
        settings.BlueskyProblem = null;
        settings.BlueskySignInRefused = false;
        settings.BlueskyStoppedUntil = null;
        settings.BlueskyPostingOn = true;
        settings.ManagedGroupId = Group;
        change?.Invoke(settings);

        if (!await context.VRChatWorlds.AnyAsync(w => w.WorldId == World, Ct))
            context.VRChatWorlds.Add(new VRChatWorld { WorldId = World, Name = "Cozy Cabin" });

        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// An open event that started ten minutes ago, with its Bluesky post for the date already on Bluesky,
    /// an instance with one person in it, and the opening that links them.
    /// </summary>
    private async Task<Scene> AddSceneAsync(
        Action<CalendarEvent>? shape = null, string parentState = PostDestinationStates.Posted, int users = 1, bool datePost = true)
    {
        var now = _clock.UtcNow;
        var startsAt = now.AddMinutes(-10);

        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            StartsAt = startsAt,
            EndsAt = startsAt.AddHours(2),
            OccurrenceStartsAt = startsAt,
            TimeZone = "Europe/London",
            Visibility = "public",
            AccessType = "public",
            PublishToBluesky = true,
            AnnounceFirstJoinOnBluesky = true,
            State = CalendarEventStates.Open,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2),
        };

        shape?.Invoke(e);

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = Location,
            WorldId = World,
            VRChatInstanceId = "12345",
            GroupId = Group,
            Type = "group",
            GroupAccessType = "plus",
            Region = "us",
            OpenedAt = startsAt,
            LastSeenAt = now,
            SeenInGroupList = true,
            LastUserCount = users,
        };

        var parentId = Guid.CreateVersion7();

        await using var context = db.NewContext();
        context.CalendarEvents.Add(e);
        context.VRChatInstances.Add(instance);
        context.CalendarOpenings.Add(new CalendarOpening
        {
            EventId = e.Id,
            OccurrenceStartsAt = startsAt,
            AttemptedAt = startsAt,
            Location = Location,
            InstanceId = instance.Id,
        });

        if (datePost)
        {
            var post = new Post
            {
                Id = Guid.CreateVersion7(),
                Title = "Movie night",
                Text = "Movie night",
                Kind = PostKinds.EventDate,
                EventId = e.Id,
                DateStartsAt = startsAt,
                Status = PostStatuses.Scheduled,
                SendAt = startsAt.AddDays(-1),
                TimeZone = "UTC",
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2),
            };

            post.Destinations.Add(new PostDestination
            {
                Id = parentId,
                PostId = post.Id,
                Network = PostNetworks.Bluesky,
                Target = FakeBluesky.Did,
                State = parentState,
                ExternalId = $"at://{FakeBluesky.Did}/app.bsky.feed.post/3l4pqjzzyde2a",
                ClientKey = "3l4pqjzzyde2a",
                Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(Cid: "bafy-parent")),
                UpdatedAt = now.AddDays(-1),
            });

            context.Posts.Add(post);
        }

        await context.SaveChangesAsync(Ct);
        return new Scene(e.Id, startsAt, instance.Id, parentId);
    }

    private async Task<int> PassAsync()
    {
        await using var context = db.NewContext();
        return await new CalendarBlueskyFirstJoin(context, _clock).RunOnceAsync(Ct);
    }

    private async Task<List<Post>> RepliesAsync(Guid eventId)
    {
        await using var context = db.NewContext();
        return await context.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.EventId == eventId && p.Kind == PostKinds.Live)
            .ToListAsync(Ct);
    }

    private async Task<CalendarOpening> OpeningAsync(Guid eventId)
    {
        await using var context = db.NewContext();
        return await context.CalendarOpenings.AsNoTracking().SingleAsync(o => o.EventId == eventId, Ct);
    }

    [Fact]
    public async Task TheFirstPersonInGetsAReplyUnderTheDatesPost_WithTheJoinLink()
    {
        await SetUpAsync();
        var scene = await AddSceneAsync();

        Assert.Equal(1, await PassAsync());

        var reply = Assert.Single(await RepliesAsync(scene.EventId));
        var destination = Assert.Single(reply.Destinations);
        Assert.Equal(PostNetworks.Bluesky, destination.Network);
        Assert.Equal(scene.ParentId, destination.ReplyToId);
        Assert.Equal(PostStatuses.Scheduled, reply.Status);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Equal(scene.StartsAt, reply.DateStartsAt);
        Assert.Equal(_clock.UtcNow, reply.SendAt);
        Assert.Equal(FakeBluesky.Did, destination.Target);

        var lines = destination.TextOverride!.Split('\n');
        Assert.Equal("We're live in Cozy Cabin.", lines[0]);
        Assert.StartsWith("https://vrchat.com/home/launch?worldId=wrld_cabin&instanceId=12345", lines[1], StringComparison.Ordinal);
        Assert.Equal(2, lines.Length);

        Assert.Equal(_clock.UtcNow, (await OpeningAsync(scene.EventId)).FirstJoinBlueskyPostedAt);
    }

    [Fact]
    public async Task AnEventAnyoneCannotJoinLinksTheGroupPageInstead()
    {
        await SetUpAsync();
        var scene = await AddSceneAsync(e => e.AccessType = "members");

        await PassAsync();

        var text = Assert.Single(Assert.Single(await RepliesAsync(scene.EventId)).Destinations).TextOverride!;
        Assert.Equal($"We're live in Cozy Cabin.\nhttps://vrchat.com/home/group/{Group}", text);
    }

    [Fact]
    public async Task OnlyOneReplyIsMade_HoweverManyPasses()
    {
        await SetUpAsync();
        var scene = await AddSceneAsync();

        Assert.Equal(1, await PassAsync());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await PassAsync());

        Assert.Single(await RepliesAsync(scene.EventId));
    }

    [Fact]
    public async Task WithNoPostForTheDateThereIsNoReply_AndTheOpeningSaysWhy()
    {
        await SetUpAsync();
        var scene = await AddSceneAsync(datePost: false);

        Assert.Equal(0, await PassAsync());

        Assert.Empty(await RepliesAsync(scene.EventId));
        var opening = await OpeningAsync(scene.EventId);
        Assert.NotNull(opening.FirstJoinBlueskyPostedAt);
        Assert.Equal(CalendarBlueskyFirstJoin.NoDatePost, opening.FirstJoinBlueskyPostError);
    }

    [Fact]
    public async Task ADatePostStillOnItsWayMakesTheReplyWait()
    {
        await SetUpAsync();
        var scene = await AddSceneAsync(parentState: PostDestinationStates.Waiting);

        Assert.Equal(0, await PassAsync());

        Assert.Empty(await RepliesAsync(scene.EventId));
        Assert.Null((await OpeningAsync(scene.EventId)).FirstJoinBlueskyPostedAt);
    }

    [Fact]
    public async Task NothingIsMadeBeforeAnyoneIsIn_OrWithTheSwitchOff_OrWhileBlueskyIsHeld()
    {
        await SetUpAsync();
        var empty = await AddSceneAsync(users: 0);
        Assert.Equal(0, await PassAsync());
        Assert.Empty(await RepliesAsync(empty.EventId));

        await SetUpAsync();
        var off = await AddSceneAsync(e => e.AnnounceFirstJoinOnBluesky = false);
        Assert.Equal(0, await PassAsync());
        Assert.Empty(await RepliesAsync(off.EventId));

        await SetUpAsync(s => s.BlueskyPostingOn = false);
        var held = await AddSceneAsync();
        Assert.Equal(0, await PassAsync());
        Assert.Empty(await RepliesAsync(held.EventId));
        Assert.Null((await OpeningAsync(held.EventId)).FirstJoinBlueskyPostedAt);
    }

    [Fact]
    public async Task WhatBlueskySaidWhenItTurnedTheReplyAwayIsKeptOnTheOpening()
    {
        await SetUpAsync();
        var scene = await AddSceneAsync();
        await PassAsync();

        await using (var context = db.NewContext())
        {
            var destination = await context.PostDestinations.SingleAsync(d => d.ReplyToId == scene.ParentId, Ct);
            destination.State = PostDestinationStates.Failed;
            destination.Error = "Bluesky refused the post.";
            await context.SaveChangesAsync(Ct);
        }

        await PassAsync();

        Assert.Equal("Bluesky refused the post.", (await OpeningAsync(scene.EventId)).FirstJoinBlueskyPostError);
    }
}
