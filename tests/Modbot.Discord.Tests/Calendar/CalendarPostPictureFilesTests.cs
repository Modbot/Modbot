using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Files;
using Modbot.Discord.Bot;
using Modbot.Discord.Calendar;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// A channel post carries exactly the files its card points at, on every post, edit and last word
/// (calendar design §15.4, changed 2026-10-02 after review). An edit used to trust the message to
/// still hold the file the first post left, and the card broke when it now wanted another one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarPostPictureFilesTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string WorldPicture = "https://api.vrchat.cloud/api/1/file/file_0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d/1/file";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The world's picture, or nothing when told to fail.</summary>
    private sealed class FakePictures(bool answers) : IPictures
    {
        public Task<PictureBytes?> FetchAsync(string? url, CancellationToken ct) =>
            Task.FromResult(answers && url == WorldPicture ? new PictureBytes(Png, "image/png") : null);
    }

    /// <summary>
    /// One pass, with its own picture source: a fresh <see cref="CardPictures"/> each time, so a
    /// failed fetch in one pass is not remembered into the next.
    /// </summary>
    private static async Task RunAsync(TestServices services, FakeGateway gateway, bool picturesAnswer = true)
    {
        using var scope = services.Scope();
        var provider = scope.ServiceProvider;

        var publisher = new CalendarDiscordPublisher(
            provider.GetRequiredService<ModbotContext>(),
            services.Clock,
            provider.GetRequiredService<DiscordBotStatus>(),
            provider.GetRequiredService<IFactWriter>(),
            provider.GetRequiredService<EventPartitionMaintainer>(),
            new CardPictures(new FakePictures(picturesAnswer)));

        await publisher.RunOnceAsync(gateway, Ct);
    }

    private async Task<TestServices> ServicesAsync()
    {
        var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.VRChatImagesProxied = true;
        }, Ct);

        var now = services.Clock.UtcNow;
        await using var context = services.Database.NewContext();
        context.VRChatWorlds.Add(new VRChatWorld
        {
            WorldId = World,
            Name = "Movie Theatre",
            ImageUrl = WorldPicture,
            FirstSeenAt = now,
            LastSeenAt = now,
        });
        await context.SaveChangesAsync(Ct);

        return services;
    }

    private static async Task<Guid> AddCoverAsync(TestServices services)
    {
        var id = Guid.CreateVersion7();

        await using var context = services.Database.NewContext();
        context.CalendarCoverPictures.Add(new CalendarCoverPicture
        {
            Id = id,
            Bytes = Png,
            ContentType = "image/png",
            CreatedAt = services.Clock.UtcNow,
        });
        await context.SaveChangesAsync(Ct);

        return id;
    }

    private static async Task<Guid> AddEventAsync(TestServices services, Guid? coverId)
    {
        var now = services.Clock.UtcNow;
        var e = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Description = "Bring snacks",
            StartsAt = now.AddDays(1),
            EndsAt = now.AddDays(1).AddHours(2),
            TimeZone = "UTC",
            WorldId = World,
            CoverPictureId = coverId,
            State = CalendarEventStates.Scheduled,
            PostToChannel = true,
            ChannelId = Channel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);

        return e.Id;
    }

    private static async Task ChangeAsync(TestServices services, Guid id, Action<CalendarEvent> change)
    {
        await using var context = services.Database.NewContext();
        var e = await context.CalendarEvents.SingleAsync(x => x.Id == id, Ct);
        change(e);
        e.UpdatedAt = services.Clock.UtcNow;
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>The card of the last edit points at a file that edit carried, or at none and carries none.</summary>
    private static void PointsOnlyAtWhatItCarries(FakeGateway gateway)
    {
        var edit = gateway.Edits[^1];
        var files = gateway.EditPictures[^1];

        // Null would mean "keep what the message has", which is what broke the card.
        Assert.NotNull(files);

        var image = edit.Embeds[0].ImageUrl;
        if (image is not null && image.StartsWith(DiscordPicture.Scheme, StringComparison.Ordinal))
            Assert.Contains(files, f => f.Reference == image);
        else
            Assert.Empty(files);
    }

    [Fact]
    public async Task RemovingTheCroppedPictureSendsTheWorldsPictureWithTheEdit()
    {
        await using var services = await ServicesAsync();
        var gateway = new FakeGateway();
        var cover = await AddCoverAsync(services);
        var id = await AddEventAsync(services, cover);

        await RunAsync(services, gateway);
        var post = Assert.Single(gateway.Messages);
        Assert.StartsWith(DiscordPicture.Scheme + CalendarDiscordPublisher.CoverName(cover), post.Embeds[0].ImageUrl, StringComparison.Ordinal);

        // What saving without the picture leaves.
        await ChangeAsync(services, id, x => x.CoverPictureId = null);
        await RunAsync(services, gateway);

        var edit = gateway.Edits[^1];
        Assert.StartsWith(DiscordPicture.Scheme, edit.Embeds[0].ImageUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("cover-", edit.Embeds[0].ImageUrl, StringComparison.Ordinal);
        PointsOnlyAtWhatItCarries(gateway);
    }

    [Fact]
    public async Task AWorldPictureThatFailsOnceIsSentAgainOnTheNextEdit()
    {
        await using var services = await ServicesAsync();
        var gateway = new FakeGateway();
        var id = await AddEventAsync(services, coverId: null);

        await RunAsync(services, gateway);
        Assert.Single(gateway.PicturesSent[Assert.Single(gateway.Messages).MessageId]!);

        // The world's picture cannot be fetched this time: the card shows none and carries none.
        await ChangeAsync(services, id, x => x.Title = "Movie night!");
        await RunAsync(services, gateway, picturesAnswer: false);

        Assert.Null(gateway.Edits[^1].Embeds[0].ImageUrl);
        PointsOnlyAtWhatItCarries(gateway);

        // It answers again: the next edit carries the file it points at.
        await ChangeAsync(services, id, x => x.Title = "Movie night!!");
        await RunAsync(services, gateway);

        Assert.StartsWith(DiscordPicture.Scheme, gateway.Edits[^1].Embeds[0].ImageUrl, StringComparison.Ordinal);
        PointsOnlyAtWhatItCarries(gateway);
    }

    /// <summary>
    /// Deleting an event deletes its cropped picture at once, before the post's last word is written:
    /// the last word carries the world's picture it falls back to.
    /// </summary>
    [Fact]
    public async Task ADeletedEventsLastWordCarriesThePictureItPointsAt()
    {
        await using var services = await ServicesAsync();
        var gateway = new FakeGateway();
        var cover = await AddCoverAsync(services);
        var id = await AddEventAsync(services, cover);

        await RunAsync(services, gateway);
        Assert.Single(gateway.Messages);

        // What the delete endpoint leaves: the event cancelled and hidden, its picture deleted.
        await ChangeAsync(services, id, x =>
        {
            x.State = CalendarEventStates.Cancelled;
            x.CancelledAt = services.Clock.UtcNow;
            x.DeletedAt = services.Clock.UtcNow;
        });

        await using (var context = services.Database.NewContext())
            await context.CalendarCoverPictures.Where(c => c.Id == cover).ExecuteDeleteAsync(Ct);

        await RunAsync(services, gateway);

        var edit = gateway.Edits[^1];
        Assert.Equal("Cancelled", edit.Embeds[0].Footer);
        Assert.DoesNotContain("cover-", edit.Embeds[0].ImageUrl ?? string.Empty, StringComparison.Ordinal);
        PointsOnlyAtWhatItCarries(gateway);
    }
}
