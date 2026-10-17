using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Bluesky;

public class BlueskyGifsTests
{
    private const string Good = "https://static.klipy.com/ii/935d7ab9d8c6202580a668421940ec81/14/af/um0L4dFH.gif?hh=240&ww=498&mp4=MCCBoQlZ&webm=KkjHgST0";

    [Theory]
    [InlineData(Good)]
    [InlineData("https://STATIC.klipy.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com/ii/a/b.gif?ww=2&mp4=x&hh=1")]
    [InlineData("  " + Good + "  ")]
    public void AKlipyMediaAddressIsAccepted(string url) => Assert.Null(BlueskyGifs.Problem(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("http://static.klipy.com/ii/a/b.gif")]
    [InlineData("http://static.klipy.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://klipy.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://api.klipy.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://media.tenor.com/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com.example.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com@example.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://user:secret@static.klipy.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com:8443/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com")]
    [InlineData("https://static.klipy.com/")]
    [InlineData("ftp://static.klipy.com/ii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com/iii/a/b.gif?hh=1&ww=2")]
    [InlineData("https://static.klipy.com/ii/")]
    [InlineData("https://static.klipy.com/ii/a/b.gif")]
    [InlineData("https://static.klipy.com/ii/a/b.gif?hh=1")]
    [InlineData("https://static.klipy.com/ii/a/b.gif?ww=2")]
    [InlineData("https://static.klipy.com/ii/a/b.gif?xhh=1&xww=2")]
    public void AnythingElseIsRefused(string? url) => Assert.Equal(BlueskyGifs.NotKlipy, BlueskyGifs.Problem(url));

    [Fact]
    public void AnAddressOverTheColumnLengthIsRefused()
    {
        var tooLong = "https://static.klipy.com/ii/" + new string('a', BlueskyGifs.MaxUrlLength) + "?hh=1&ww=2";

        Assert.Equal(BlueskyGifs.NotKlipy, BlueskyGifs.Problem(tooLong));
    }

    [Fact]
    public void ThePostCarriesTheGifAsALinkCardWithItsAddressAndAltText_AndNoUploadedPicture()
    {
        var gif = new BlueskyPostGif(Good, "A cat waves");

        var record = BlueskyPostRecord.Build("Game night", DateTimeOffset.UnixEpoch, BlueskyGifs.Card(gif));

        var embed = record["embed"]!;
        Assert.Equal("app.bsky.embed.external", embed["$type"]!.GetValue<string>());
        var external = embed["external"]!;
        Assert.Equal(Good, external["uri"]!.GetValue<string>());
        Assert.Equal("A cat waves", external["title"]!.GetValue<string>());
        Assert.Equal("Alt: A cat waves", external["description"]!.GetValue<string>());
        Assert.Null(external["thumb"]);
        Assert.Null(embed["images"]);
    }

    [Theory]
    [InlineData("", "Game night", "Game night", "ALT: Game night")]
    [InlineData("   ", null, "GIF", "ALT: GIF")]
    [InlineData("", "  ", "GIF", "ALT: GIF")]
    [InlineData(" A cat ", "Game night", "A cat", "Alt: A cat")]
    public void WithoutAltTextTheCardSaysTheTitleInCapitals_NeverABareAlt(string alt, string? postTitle, string title, string description)
    {
        var card = BlueskyGifs.Card(new BlueskyPostGif(Good, alt), postTitle);

        Assert.Equal(title, card.Title);
        Assert.Equal(description, card.Description);
    }

    [Fact]
    public void NoGifGivesTheFingerprintFromBeforeGifsExisted()
    {
        var picture = Guid.CreateVersion7();
        List<BlueskyPostPicture> pictures = [new(picture, "x")];

        Assert.Equal(CalendarFingerprint.Of("one", picture), CalendarBlueskyText.Fingerprint("one", picture, gif: null));
        Assert.Equal(
            CalendarBlueskyText.Fingerprint("one", picture, pictures, [BlueskyPostGates.Nobody], true, ["en"], null),
            CalendarBlueskyText.Fingerprint("one", picture, pictures, [BlueskyPostGates.Nobody], true, ["en"], null, null));
        Assert.Equal(
            CalendarBlueskyText.Fingerprint("one", null, video: new BlueskyPostVideo(null, "https://example.com/a.mp4", "x")),
            CalendarBlueskyText.Fingerprint("one", null, video: new BlueskyPostVideo(null, "https://example.com/a.mp4", "x"), gif: null));
    }

    [Fact]
    public void AGifChangesTheFingerprint_AndSoDoesItsAddressAndItsAlt()
    {
        var plain = CalendarBlueskyText.Fingerprint("one", null);

        var withGif = CalendarBlueskyText.Fingerprint("one", null, gif: new BlueskyPostGif(Good, "x"));
        var otherAddress = CalendarBlueskyText.Fingerprint("one", null, gif: new BlueskyPostGif(Good + "1", "x"));
        var otherAlt = CalendarBlueskyText.Fingerprint("one", null, gif: new BlueskyPostGif(Good, "y"));

        Assert.Equal(4, new[] { plain, withGif, otherAddress, otherAlt }.Distinct().Count());
        Assert.Equal(withGif, CalendarBlueskyText.Fingerprint("one", null, gif: new BlueskyPostGif(Good, "x")));
    }

    [Fact]
    public void ABlankAltTextSaysThePostsTitle_AndNoGifIsNull()
    {
        var withGif = new CalendarEvent { BlueskyGif = new BlueskyPostGif(Good, "  ") };

        Assert.Equal("Game night", CalendarBlueskyText.GifOf(withGif, "Game night")!.Alt);
        Assert.Equal(string.Empty, CalendarBlueskyText.GifOf(withGif, null)!.Alt);
        Assert.Null(CalendarBlueskyText.GifOf(new CalendarEvent(), "Game night"));
    }
}
