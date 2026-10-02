using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Calendar;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Calendar;

/// <summary>
/// An event's picture link on Discord (calendar design §15.2, added 2026-10-02). A link anywhere but
/// VRChat goes on the post as it is and is the cover's address. A link on VRChat, which serves no one
/// without a session, is never put on a post for Discord to fetch: it is sent with the message like
/// the world's picture, and the cover is fetched through Modbot's VRChat side -- both only while the
/// operator lets this server fetch VRChat pictures.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarPictureOnDiscordTests(PostgresFixture db)
{
    private const string Guild = "111111111111111111";
    private const string Channel = "222222222222222222";
    private const string VRChatLink = "https://api.vrchat.cloud/api/1/file/file_6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7/1/file";
    private const string OtherLink = "https://pictures.example/movie.png";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task RunAsync(TestServices services, FakeGateway gateway)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<CalendarDiscordPublisher>().RunOnceAsync(gateway, Ct);
    }

    private static async Task AddEventAsync(TestServices services, string imageUrl, Guid? coverId = null)
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
            ImageUrl = imageUrl,
            CoverPictureId = coverId,
            State = CalendarEventStates.Scheduled,
            PublishToDiscord = true,
            PostToChannel = true,
            ChannelId = Channel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        CalendarTimeline.Advance(e, now);

        await using var context = services.Database.NewContext();
        context.CalendarEvents.Add(e);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task APictureLinkElsewhereIsOnThePostAndIsTheCover()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        await AddEventAsync(services, OtherLink);
        await RunAsync(services, gateway);

        Assert.Equal(OtherLink, Assert.Single(gateway.ServerEvents.Values).Details.CoverImageUrl);
        Assert.Equal(OtherLink, Assert.Single(gateway.Messages).Embeds[0].ImageUrl);
    }

    [Fact]
    public async Task AVRChatLinkIsNeverPutOnThePostForDiscordToFetch()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();

        await AddEventAsync(services, VRChatLink);
        await RunAsync(services, gateway);

        // No picture source in these tests, so nothing is sent with the message either.
        Assert.NotEqual(VRChatLink, Assert.Single(gateway.Messages).Embeds[0].ImageUrl);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, VRChatLink)]
    public async Task AVRChatCoverIsFetchedOnlyWhileVRChatPicturesAreOn(bool on, string? cover)
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.VRChatImagesProxied = on;
        }, Ct);
        var gateway = new FakeGateway();

        await AddEventAsync(services, VRChatLink);
        await RunAsync(services, gateway);

        Assert.Equal(cover, Assert.Single(gateway.ServerEvents.Values).Details.CoverImageUrl);
    }

    /// <summary>
    /// The picture cropped for Discord (§15.4) goes in place of the link: as the cover's bytes, and
    /// as a file on the post that the card points at.
    /// </summary>
    [Fact]
    public async Task ACroppedPictureIsTheCoverAndThePostsPicture()
    {
        await using var services = await TestServices.CreateAsync(db, Ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, Ct);
        var gateway = new FakeGateway();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var coverId = Guid.CreateVersion7();

        await using (var context = services.Database.NewContext())
        {
            context.CalendarCoverPictures.Add(new CalendarCoverPicture
            {
                Id = coverId,
                Bytes = png,
                ContentType = "image/png",
                CreatedAt = services.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        await AddEventAsync(services, OtherLink, coverId);
        await RunAsync(services, gateway);

        var details = Assert.Single(gateway.ServerEvents.Values).Details;
        Assert.Null(details.CoverImageUrl);
        Assert.Equal(png, details.CoverBytes);

        var name = CalendarDiscordPublisher.CoverName(coverId) + ".png";
        var post = Assert.Single(gateway.Messages);
        Assert.Equal("attachment://" + name, post.Embeds[0].ImageUrl);

        var file = Assert.Single(gateway.PicturesSent[post.MessageId]!);
        Assert.Equal(name, file.Name);
        Assert.Equal(png, file.Bytes);
    }

    [Theory]
    [InlineData(VRChatLink, true)]
    [InlineData("https://files.vrchat.cloud/thumbnails/1.png", true)]
    [InlineData(OtherLink, false)]
    [InlineData("https://vrchat.com/home/group", false)]
    [InlineData(null, false)]
    public void OnlyVRChatsFileHostsCount(string? url, bool expected)
    {
        Assert.Equal(expected, CalendarDiscordPublisher.IsOnVRChat(url));
    }
}
