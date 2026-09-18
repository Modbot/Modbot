using Modbot.Core.Files;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// Which pictures a message carries, what they are called, and the many ways a card ends up with
/// none. Nothing here reaches VRChat or Discord: the picture source is a fake.
/// </summary>
public class CardPicturesTests
{
    private const string Face = "https://api.vrchat.cloud/api/1/file/file_a/1/file";
    private const string Banner = "https://api.vrchat.cloud/api/1/file/file_b/1/file";

    /// <summary>A picture source that answers from a table and counts what it was asked for.</summary>
    private sealed class FakePictures : IPictures
    {
        private readonly Dictionary<string, PictureBytes?> _held = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public Exception? Throws { get; set; }

        public FakePictures Holding(string url, string contentType = "image/png", int bytes = 64)
        {
            _held[url] = new PictureBytes(new byte[bytes], contentType);
            return this;
        }

        public Task<PictureBytes?> FetchAsync(string? url, CancellationToken ct)
        {
            Asked.Add(url ?? "<null>");

            if (Throws is { } error)
                throw error;

            return Task.FromResult(url is not null && _held.TryGetValue(url, out var held) ? held : null);
        }
    }

    // ── Naming ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The same address always names the same file. That is what lets ten cards about one person
    /// in one message cost one upload, and what lets a rewrite point at a file it did not send.
    /// </summary>
    [Fact]
    public void TheSameAddressAlwaysNamesTheSameFile()
    {
        var one = CardPictures.Named(Face, new PictureBytes([1, 2, 3], "image/png"));
        var two = CardPictures.Named(Face, new PictureBytes([9, 9], "image/png"));

        Assert.NotNull(one);
        Assert.Equal(one.Name, two!.Name);
        Assert.NotEqual(one.Name, CardPictures.Named(Banner, new PictureBytes([1], "image/png"))!.Name);
    }

    /// <summary>Discord decides what a file is from its name, so the extension comes from the bytes.</summary>
    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("IMAGE/PNG; charset=binary", ".png")]
    public void TheNameEndsInTheExtensionForTheBytes(string contentType, string extension)
        => Assert.EndsWith(extension, CardPictures.Named(Face, new PictureBytes([1], contentType))!.Name, StringComparison.Ordinal);

    /// <summary>
    /// VRChat serves video from these addresses too, and an embed has nowhere to put one.
    /// </summary>
    [Theory]
    [InlineData("video/mp4")]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData(null)]
    public void BytesACardCannotShow_AreNoPicture(string? contentType)
        => Assert.Null(CardPictures.Named(Face, new PictureBytes([1], contentType!)));

    [Fact]
    public void BytesTooLargeForAMessage_AreNoPicture()
        => Assert.Null(CardPictures.Named(Face, new PictureBytes(new byte[CardPictures.MaxBytes + 1], "image/png")));

    [Fact]
    public void NoBytesAtAll_AreNoPicture()
        => Assert.Null(CardPictures.Named(Face, new PictureBytes([], "image/png")));

    // ── Choosing, and falling back ───────────────────────────────────────────────────────

    [Fact]
    public async Task APictureThatCanBeFetched_IsSentAndPointedAt()
    {
        var message = new CardPictures(new FakePictures().Holding(Face)).ForMessage();

        var reference = await message.AddAsync(Face, TestContext.Current.CancellationToken);

        var file = Assert.Single(message.Files);
        Assert.Equal(DiscordPicture.Scheme + file.Name, reference);
        Assert.StartsWith(DiscordPicture.Scheme, reference, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule the whole design rests on: anything that goes wrong is a card with no picture,
    /// never a broken picture and never a post that did not happen.
    /// </summary>
    [Fact]
    public async Task APictureThatCannotBeFetched_IsNoPictureAndNoFile()
    {
        var message = new CardPictures(new FakePictures()).ForMessage();

        Assert.Null(await message.AddAsync(Face, TestContext.Current.CancellationToken));
        Assert.Empty(message.Files);
    }

    [Fact]
    public async Task APictureSourceThatThrows_IsNoPictureAndNoPostLost()
    {
        var source = new FakePictures { Throws = new InvalidOperationException("VRChat is having a moment") };
        var message = new CardPictures(source).ForMessage();

        Assert.Null(await message.AddAsync(Face, TestContext.Current.CancellationToken));
        Assert.Empty(message.Files);
    }

    [Fact]
    public async Task WithNoPictureSourceAtAll_EveryCardGoesWithoutOne()
    {
        var message = new CardPictures().ForMessage();

        Assert.Null(await message.AddAsync(Face, TestContext.Current.CancellationToken));
        Assert.Empty(message.Files);
    }

    /// <summary>The operator's switch for fetching VRChat pictures through this server.</summary>
    [Fact]
    public async Task WithPicturesTurnedOff_NothingIsEvenAskedFor()
    {
        var source = new FakePictures().Holding(Face);
        var message = new CardPictures(source).ForMessage(on: false);

        Assert.Null(await message.AddAsync(Face, TestContext.Current.CancellationToken));
        Assert.Empty(message.Files);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task NoAddressIsNoPicture()
    {
        var source = new FakePictures();
        var message = new CardPictures(source).ForMessage();

        Assert.Null(await message.AddAsync(null, TestContext.Current.CancellationToken));
        Assert.Null(await message.AddAsync("   ", TestContext.Current.CancellationToken));
        Assert.Empty(source.Asked);
    }

    // ── One message, many cards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoCardsAboutOnePerson_ShareOneFile()
    {
        var message = new CardPictures(new FakePictures().Holding(Face)).ForMessage();

        var first = await message.AddAsync(Face, TestContext.Current.CancellationToken);
        var second = await message.AddAsync(Face, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Single(message.Files);
    }

    /// <summary>
    /// Discord refuses a message with an eleventh file by refusing the whole message. The card that
    /// does not fit loses its picture; the message still goes out.
    /// </summary>
    [Fact]
    public async Task PastDiscordsLimit_ACardLosesItsPictureRatherThanTheMessage()
    {
        var source = new FakePictures();
        for (var i = 0; i <= DiscordPicture.PerMessage; i++)
            source.Holding($"https://api.vrchat.cloud/api/1/file/file_{i}/1/file");

        var message = new CardPictures(source).ForMessage();

        for (var i = 0; i < DiscordPicture.PerMessage; i++)
        {
            Assert.NotNull(await message.AddAsync(
                $"https://api.vrchat.cloud/api/1/file/file_{i}/1/file", TestContext.Current.CancellationToken));
        }

        Assert.Null(await message.AddAsync(
            $"https://api.vrchat.cloud/api/1/file/file_{DiscordPicture.PerMessage}/1/file",
            TestContext.Current.CancellationToken));

        Assert.Equal(DiscordPicture.PerMessage, message.Files.Count);
    }

    // ── Paid for once ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A card that is rewritten every minute for hours points at the file the first post left on
    /// the message, and sends nothing.
    /// </summary>
    [Fact]
    public async Task ARewritePointsAtTheFileTheFirstPostSent_AndSendsNothing()
    {
        var pictures = new CardPictures(new FakePictures().Holding(Face));

        var first = pictures.ForMessage();
        var posted = await first.AddAsync(Face, TestContext.Current.CancellationToken);
        Assert.Single(first.Files);

        var rewrite = pictures.ForMessage();
        var again = await rewrite.ReferenceAsync(Face, TestContext.Current.CancellationToken);

        Assert.Equal(posted, again);
        Assert.Empty(rewrite.Files);
    }

    [Fact]
    public async Task ARewriteOfAPictureThatIsGone_PointsAtNothing()
    {
        var rewrite = new CardPictures(new FakePictures()).ForMessage();

        Assert.Null(await rewrite.ReferenceAsync(Face, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// What is fetched is remembered, across messages and across passes, so the rewrite above is
    /// free rather than a fetch every minute.
    /// </summary>
    [Fact]
    public async Task AnAddressIsFetchedOnce()
    {
        var source = new FakePictures().Holding(Face);
        var pictures = new CardPictures(source);

        await pictures.ForMessage().AddAsync(Face, TestContext.Current.CancellationToken);
        await pictures.ForMessage().AddAsync(Face, TestContext.Current.CancellationToken);
        await pictures.ForMessage().ReferenceAsync(Face, TestContext.Current.CancellationToken);

        Assert.Single(source.Asked);
    }

    /// <summary>
    /// Remembering the failures matters more than remembering the pictures: somebody with no
    /// picture would otherwise be asked for on every pass for as long as their card exists.
    /// </summary>
    [Fact]
    public async Task AnAddressWithNoPicture_IsNotAskedForAgain()
    {
        var source = new FakePictures();
        var pictures = new CardPictures(source);

        await pictures.ForMessage().AddAsync(Face, TestContext.Current.CancellationToken);
        await pictures.ForMessage().AddAsync(Face, TestContext.Current.CancellationToken);

        Assert.Single(source.Asked);
    }

    [Fact]
    public async Task ACardCanTakeItsTwoPicturesAtOnce()
    {
        var source = new FakePictures().Holding(Face).Holding(Banner);
        var message = new CardPictures(source).ForMessage();

        var picture = await message.ForCardAsync(Face, Banner, TestContext.Current.CancellationToken);

        Assert.NotNull(picture.Thumbnail);
        Assert.NotNull(picture.Image);
        Assert.NotEqual(picture.Thumbnail, picture.Image);
        Assert.Equal(2, message.Files.Count);
    }

    [Fact]
    public void ACardWithNoPicturesIsTheDefault()
    {
        Assert.Null(CardPicture.None.Thumbnail);
        Assert.Null(CardPicture.None.Image);
        Assert.Null(CardPicture.None.AuthorIcon);
    }
}
