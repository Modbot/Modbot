using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Posts;

/// <summary>
/// The <c>post</c> and <c>post_destination</c> tables against real PostgreSQL (posts design §2.2):
/// the claim's version check, the unique indexes, and the picture sweep counting a post as a use.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PostTablesTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Post> AddPostAsync(Action<Post>? shape = null)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Text = "Movie night",
            Status = PostStatuses.Scheduled,
            SendAt = Now,
            TimeZone = "UTC",
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        post.Destinations.Add(new PostDestination
        {
            Id = Guid.NewGuid(),
            PostId = post.Id,
            Network = PostNetworks.Discord,
            Target = "222222222222222222",
            State = PostDestinationStates.Waiting,
            UpdatedAt = Now,
        });

        shape?.Invoke(post);

        await using var context = db.NewContext();
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);
        return post;
    }

    [Fact]
    public async Task AClaimWritesSendingAndWhatGoesOut_BeforeAnythingIsSent()
    {
        var added = await AddPostAsync();

        await using (var context = db.NewContext())
        {
            var post = await context.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == added.Id, Ct);
            var claimed = await new PostClaim(context, new FakeClock(Now)).ClaimAsync(post, post.Destinations[0], null, "Movie night", Ct);
            Assert.True(claimed);
        }

        await using var read = db.NewContext();
        var row = await read.PostDestinations.AsNoTracking().SingleAsync(d => d.PostId == added.Id, Ct);
        Assert.Equal(PostDestinationStates.Sending, row.State);
        Assert.Equal(Now, row.SentAt);
        Assert.Equal("Movie night", row.SentText);
        Assert.Equal(1, (await read.Posts.AsNoTracking().SingleAsync(p => p.Id == added.Id, Ct)).Version);
    }

    /// <summary>
    /// An edit read before the claim and saved after it fails its own check: the words already on
    /// their way are never changed under the sender (posts design §3.4).
    /// </summary>
    [Fact]
    public async Task AnEditSavedDuringASendIsRefusedByTheVersion()
    {
        var added = await AddPostAsync();

        await using var editor = db.NewContext();
        var edited = await editor.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == added.Id, Ct);

        await using (var sender = db.NewContext())
        {
            var post = await sender.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == added.Id, Ct);
            Assert.True(await new PostClaim(sender, new FakeClock(Now)).ClaimAsync(post, post.Destinations[0], null, "Movie night", Ct));
        }

        edited.Text = "Something else";
        edited.Version++;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => editor.SaveChangesAsync(Ct));
    }

    /// <summary>An edit saved first makes the claim fail, and nothing is sent until the post is read again.</summary>
    [Fact]
    public async Task AClaimAfterAnEditIsRefused()
    {
        var added = await AddPostAsync();

        await using var sender = db.NewContext();
        var post = await sender.Posts.Include(p => p.Destinations).SingleAsync(p => p.Id == added.Id, Ct);

        await using (var editor = db.NewContext())
        {
            var edited = await editor.Posts.SingleAsync(p => p.Id == added.Id, Ct);
            edited.Text = "Something else";
            edited.Version++;
            await editor.SaveChangesAsync(Ct);
        }

        Assert.False(await new PostClaim(sender, new FakeClock(Now)).ClaimAsync(post, post.Destinations[0], null, "Movie night", Ct));

        await using var read = db.NewContext();
        Assert.Equal(PostDestinationStates.Waiting, (await read.PostDestinations.AsNoTracking().SingleAsync(d => d.PostId == added.Id, Ct)).State);
    }

    [Fact]
    public async Task OnePostHasOneRowPerSite()
    {
        var added = await AddPostAsync();

        await using var context = db.NewContext();
        context.PostDestinations.Add(new PostDestination
        {
            Id = Guid.NewGuid(),
            PostId = added.Id,
            Network = PostNetworks.Discord,
            Target = "333",
            UpdatedAt = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task TheCalendarCannotMakeASecondPostOfOneKindForAnEvent()
    {
        var eventId = Guid.NewGuid();
        await AddPostAsync(p => { p.EventId = eventId; p.Kind = PostKinds.Announced; });

        await Assert.ThrowsAsync<DbUpdateException>(() => AddPostAsync(p => { p.EventId = eventId; p.Kind = PostKinds.Announced; }));
    }

    [Fact]
    public async Task OneReminderPerDate_ButEachDateHasItsOwn()
    {
        var eventId = Guid.NewGuid();
        await AddPostAsync(p => { p.EventId = eventId; p.Kind = PostKinds.Reminder; p.DateStartsAt = Now; });
        await AddPostAsync(p => { p.EventId = eventId; p.Kind = PostKinds.Reminder; p.DateStartsAt = Now.AddDays(7); });

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddPostAsync(p => { p.EventId = eventId; p.Kind = PostKinds.Reminder; p.DateStartsAt = Now; }));
    }

    [Fact]
    public async Task PostsPeopleWriteAreNeverHeldBackByTheIndexes()
    {
        await AddPostAsync();
        await AddPostAsync();
    }

    /// <summary>The hourly sweep keeps a picture a post uses, whatever the post's state (posts design §2.2).</summary>
    [Fact]
    public async Task TheSweepKeepsAPictureAPostUses()
    {
        var used = Guid.NewGuid();
        var unused = Guid.NewGuid();

        await using (var context = db.NewContext())
        {
            foreach (var id in new[] { used, unused })
            {
                context.CalendarCoverPictures.Add(new CalendarCoverPicture
                {
                    Id = id,
                    Bytes = [0x89, 0x50, 0x4E, 0x47],
                    ContentType = "image/png",
                    CreatedAt = Now - TimeSpan.FromDays(3),
                });
            }

            await context.SaveChangesAsync(Ct);
        }

        await AddPostAsync(p => { p.PictureId = used; p.Status = PostStatuses.Cancelled; });

        await using (var context = db.NewContext())
            await new CalendarCoverSweep(context, new FakeClock(Now)).RunOnceAsync(Ct);

        await using var read = db.NewContext();
        Assert.True(await read.CalendarCoverPictures.AnyAsync(c => c.Id == used, Ct));
        Assert.False(await read.CalendarCoverPictures.AnyAsync(c => c.Id == unused, Ct));
    }

    /// <summary>
    /// The sweep keeps a site's own copy of a post's picture too: Bluesky's small card picture, which
    /// lives on the destination, not the post (posts design §4.2c).
    /// </summary>
    [Fact]
    public async Task TheSweepKeepsBlueskysCardPicture()
    {
        var copy = Guid.NewGuid();

        await using (var context = db.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture
            {
                Id = copy,
                Bytes = [0xFF, 0xD8, 0xFF, 0xE0],
                ContentType = "image/jpeg",
                CreatedAt = Now - TimeSpan.FromDays(3),
            });

            await context.SaveChangesAsync(Ct);
        }

        await AddPostAsync(p =>
        {
            var destination = p.Destinations[0];
            destination.Network = PostNetworks.Bluesky;
            destination.Target = "did:plc:ewvi7nxzyoun6zhxrhs64oiz";
            destination.SitePictureId = copy;
        });

        await using (var context = db.NewContext())
            await new CalendarCoverSweep(context, new FakeClock(Now)).RunOnceAsync(Ct);

        await using var read = db.NewContext();
        Assert.True(await read.CalendarCoverPictures.AnyAsync(c => c.Id == copy, Ct));
    }

    /// <summary>A card picture deleted under a destination leaves it with none, as a post's own picture does.</summary>
    [Fact]
    public async Task ACardPictureDeletedLeavesTheDestinationWithNone()
    {
        var copy = Guid.NewGuid();

        await using (var context = db.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture
            {
                Id = copy,
                Bytes = [0xFF, 0xD8, 0xFF, 0xE0],
                ContentType = "image/jpeg",
                CreatedAt = Now,
            });

            await context.SaveChangesAsync(Ct);
        }

        var post = await AddPostAsync(p =>
        {
            p.Destinations[0].Network = PostNetworks.Bluesky;
            p.Destinations[0].SitePictureId = copy;
        });

        await using (var context = db.NewContext())
            await context.CalendarCoverPictures.Where(c => c.Id == copy).ExecuteDeleteAsync(Ct);

        await using var read = db.NewContext();
        var destination = await read.PostDestinations.AsNoTracking().SingleAsync(d => d.PostId == post.Id, Ct);
        Assert.Null(destination.SitePictureId);
    }
}
