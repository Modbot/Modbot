using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Net;
using Modbot.Discord.Gallery;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Gallery;

/// <summary>
/// The pictures members post in the gallery channel: only that channel, only people, only a PNG or
/// JPEG that is not too big (judged by its first bytes, not by what Discord called it), each picture
/// once however often the message is seen, and no more than a hundred waiting at a time. Nothing here
/// reaches the network: the handler answers from a table.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GallerySubmissionIntakeTests(PostgresFixture db)
{
    private const string Channel = "222222222222222222";
    private const string Guild = "111111111111111111";
    private const string PngLink = "https://cdn.example/sunset.png";
    private const string GifLink = "https://cdn.example/dance.png";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static readonly byte[] GifBytes = "GIF89a"u8.ToArray().Concat(new byte[] { 1, 2, 3, 4 }).ToArray();

    private sealed class Table : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Bytes, string Type)> _answers = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public Table Picture(string url, byte[] bytes, string type = "image/png")
        {
            _answers[url] = (bytes, type);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);

            if (!_answers.TryGetValue(url, out var answer))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var content = new ByteArrayContent(answer.Bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(answer.Type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static DiscordMessageSnapshot Message(
        string id = "300000000000000001",
        string channel = Channel,
        bool bot = false,
        params DiscordAttachmentSnapshot[] attachments) => new(
            id, Guild, channel, null, "42", "Rowan", bot, Day, null, "", attachments, 0, null, 0, false);

    private static DiscordAttachmentSnapshot Png(string url = PngLink, string name = "sunset.png", string? type = "image/png", long size = 12)
        => new(name, type, size, url);

    private async Task<(IsolatedDatabase Database, ModbotContext Context)> StartAsync(string? channel = Channel)
    {
        var database = await IsolatedDatabase.CreateAsync(db, Ct);
        var context = database.NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGalleryChannelId = channel;
        await context.SaveChangesAsync(Ct);

        return (database, context);
    }

    private static GallerySubmissionIntake Intake(ModbotContext context, Table table)
        => new(context, new FakeClock(Day), new PictureLinks(table));

    [Fact]
    public async Task APictureInTheChosenChannelIsQueuedWithItsBytes()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes);

        var queued = await Intake(context, table).TakeAsync(Message(attachments: [Png()]), Ct);

        Assert.Equal(1, queued);

        var row = await context.GallerySubmissions.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(GallerySubmissionStatus.Waiting, row.Status);
        Assert.Equal("image/png", row.ContentType);
        Assert.Equal(PngBytes, row.Bytes);
        Assert.Equal("42", row.AuthorDiscordId);
        Assert.Equal("Rowan", row.AuthorName);
        Assert.Equal("sunset.png", row.FileName);
        Assert.Equal($"https://discord.com/channels/{Guild}/{Channel}/300000000000000001", row.MessageUrl);
        Assert.Equal(Channel, row.DiscordChannelId);
    }

    [Fact]
    public async Task APictureInAnotherChannelIsLeftAlone_AndNeverFetched()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes);

        var queued = await Intake(context, table).TakeAsync(Message(channel: "999", attachments: [Png()]), Ct);

        Assert.Equal(0, queued);
        Assert.Empty(table.Asked);
        Assert.False(await context.GallerySubmissions.AnyAsync(Ct));
    }

    [Fact]
    public async Task WithNoChannelChosen_NothingIsQueued()
    {
        var (database, context) = await StartAsync(channel: null);
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes);

        Assert.Equal(0, await Intake(context, table).TakeAsync(Message(attachments: [Png()]), Ct));
        Assert.Empty(table.Asked);
    }

    [Fact]
    public async Task ABotsPictureIsLeftAlone()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes);

        Assert.Equal(0, await Intake(context, table).TakeAsync(Message(bot: true, attachments: [Png()]), Ct));
        Assert.Empty(table.Asked);
    }

    [Theory]
    [InlineData("image/gif", "dance.gif", 12L)]
    [InlineData("application/pdf", "form.pdf", 12L)]
    [InlineData("image/png", "huge.png", PictureLinks.MaxBytes + 1L)]
    [InlineData("image/png", "empty.png", 0L)]
    public async Task AFileThatIsNotAPictureOrIsTooBigIsNeverFetched(string type, string name, long size)
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes);

        var queued = await Intake(context, table).TakeAsync(
            Message(attachments: [Png(type: type, name: name, size: size)]), Ct);

        Assert.Equal(0, queued);
        Assert.Empty(table.Asked);
    }

    [Fact]
    public async Task AGifCalledAPngIsSkippedByItsBytes()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(GifLink, GifBytes);

        var queued = await Intake(context, table).TakeAsync(
            Message(attachments: [Png(GifLink, "dance.png")]), Ct);

        Assert.Equal(0, queued);
        Assert.False(await context.GallerySubmissions.AnyAsync(Ct));
    }

    [Fact]
    public async Task TheSameMessageSeenTwiceIsQueuedOnce()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes);
        var intake = Intake(context, table);
        var message = Message(attachments: [Png()]);

        Assert.Equal(1, await intake.TakeAsync(message, Ct));
        Assert.Equal(0, await intake.TakeAsync(message, Ct));

        Assert.Equal(1, await context.GallerySubmissions.CountAsync(Ct));
        Assert.Single(table.Asked);
    }

    [Fact]
    public async Task AMessageWithTwoPicturesIsTwoRows()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;
        var table = new Table().Picture(PngLink, PngBytes).Picture("https://cdn.example/two.png", PngBytes);

        var queued = await Intake(context, table).TakeAsync(
            Message(attachments: [Png(), Png("https://cdn.example/two.png", "two.png")]), Ct);

        Assert.Equal(2, queued);
        Assert.Equal([0, 1], await context.GallerySubmissions.OrderBy(s => s.AttachmentIndex).Select(s => s.AttachmentIndex).ToListAsync(Ct));
    }

    [Fact]
    public async Task AtAHundredWaiting_NoMoreAreKept()
    {
        var (database, context) = await StartAsync();
        await using var _db = database;
        await using var _context = context;

        for (var i = 0; i < GallerySubmission.MaxWaiting; i++)
        {
            context.GallerySubmissions.Add(new GallerySubmission
            {
                Id = Guid.NewGuid(),
                DiscordMessageId = $"1000{i}",
                DiscordChannelId = Channel,
                AuthorDiscordId = "7",
                AuthorName = "Other",
                Bytes = PngBytes,
                SubmittedAt = Day,
            });
        }

        await context.SaveChangesAsync(Ct);

        var table = new Table().Picture(PngLink, PngBytes);

        var queued = await Intake(context, table).TakeAsync(Message(attachments: [Png()]), Ct);

        Assert.Equal(0, queued);
        Assert.Empty(table.Asked);
        Assert.Equal(GallerySubmission.MaxWaiting, await context.GallerySubmissions.CountAsync(Ct));
    }
}
