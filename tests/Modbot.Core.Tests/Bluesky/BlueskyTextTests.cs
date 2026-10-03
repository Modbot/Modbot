using System.Text;
using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>
/// A Bluesky post's text (Bluesky design §3.5): counted in graphemes and UTF-8 bytes the way Bluesky
/// counts, links and tags marked by UTF-8 byte ranges, never a mention, and the card built for the
/// first link.
/// </summary>
public class BlueskyTextTests
{
    /// <summary>The bytes a facet covers, read back out of the text.</summary>
    private static string Covered(string text, BlueskyFacet facet) =>
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text), facet.Start, facet.End - facet.Start);

    [Fact]
    public void ALinkIsMarkedByItsUtf8Bytes()
    {
        const string text = "Movie night https://vrchat.com/home/world/wrld_1 tonight";

        var facet = Assert.Single(BlueskyText.Facets(text));

        Assert.Equal(BlueskyText.LinkKind, facet.Kind);
        Assert.Equal("https://vrchat.com/home/world/wrld_1", facet.Value);
        Assert.Equal(12, facet.Start);
        Assert.Equal(facet.Value, Covered(text, facet));
    }

    [Theory]
    [InlineData("🎬🍿 Film https://example.com/a")]
    [InlineData("映画の夜 https://example.com/a")]
    [InlineData("Café night https://example.com/a")]
    [InlineData("👨‍👩‍👧‍👦 family https://example.com/a")]
    [InlineData("👍🏽 https://example.com/a")]
    public void OffsetsAreBytesNotCharacters_WhateverComesBefore(string text)
    {
        var facet = Assert.Single(BlueskyText.Facets(text));

        Assert.Equal("https://example.com/a", Covered(text, facet));
        Assert.Equal(Encoding.UTF8.GetByteCount(text[..text.IndexOf("https", StringComparison.Ordinal)]), facet.Start);
    }

    [Fact]
    public void ASentenceEndAfterALinkIsNotPartOfIt()
    {
        var facets = BlueskyText.Facets("See https://example.com/page. And (https://example.com/b) too!");

        Assert.Equal(["https://example.com/page", "https://example.com/b"], facets.Select(f => f.Value));
    }

    [Fact]
    public void ALinkThatOpensABracketKeepsItsClosingOne()
    {
        var facet = Assert.Single(BlueskyText.Facets("Read https://en.wikipedia.org/wiki/Foo_(bar) now"));

        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", facet.Value);
    }

    [Fact]
    public void TagsAreMarkedWithoutTheirHash_AndOnlyDigitsIsNotATag()
    {
        const string text = "Join us #VRChat #movienight, #2026 tonight";

        var tags = BlueskyText.Facets(text).Where(f => f.Kind == BlueskyText.TagKind).ToList();

        Assert.Equal(["VRChat", "movienight"], tags.Select(t => t.Value));
        Assert.All(tags, t => Assert.StartsWith("#", Covered(text, t), StringComparison.Ordinal));
        Assert.Equal("#movienight", Covered(text, tags[1]));
    }

    [Fact]
    public void ATagLongerThan64CharactersIsLeftAsText()
    {
        var text = "#" + new string('a', 65);

        Assert.Empty(BlueskyText.Facets(text));
    }

    [Fact]
    public void AHandleIsNeverMarked_SoNobodyIsNotified()
    {
        const string text = "Thanks @someone.bsky.social and @friend for coming";

        Assert.Empty(BlueskyText.Facets(text));

        var record = BlueskyPostRecord.Build(text, DateTimeOffset.UnixEpoch, card: null, thumb: null);
        Assert.False(record.ContainsKey("facets"));
        Assert.DoesNotContain("#mention", record.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeHundredGraphemesFit_AndOneMoreDoesNot()
    {
        // A family emoji is one grapheme of eleven UTF-16 units and 25 bytes.
        var fits = string.Concat(Enumerable.Repeat("a", 299)) + "👨‍👩‍👧‍👦";
        var over = fits + "b";

        Assert.Equal(300, BlueskyText.Graphemes(fits));
        Assert.True(BlueskyText.Fits(fits));
        Assert.Equal(301, BlueskyText.Graphemes(over));
        Assert.False(BlueskyText.Fits(over));
    }

    [Fact]
    public void ThreeThousandBytesIsTheOtherLimit()
    {
        // 120 family emoji: 120 graphemes, 3000 bytes. One more is 125 bytes over.
        var family = "👨‍👩‍👧‍👦";
        Assert.Equal(25, Encoding.UTF8.GetByteCount(family));

        var fits = string.Concat(Enumerable.Repeat(family, 120));
        var over = fits + family;

        Assert.True(BlueskyText.Fits(fits));
        Assert.Equal(121, BlueskyText.Graphemes(over));
        Assert.False(BlueskyText.Fits(over));
    }

    [Fact]
    public void CombiningMarksCountAsTheLetterTheyMark()
    {
        Assert.Equal(4, BlueskyText.Graphemes("Café"));
        Assert.Equal(1, BlueskyText.Graphemes("👍🏽"));
        Assert.Equal(4, BlueskyText.Graphemes("映画の夜"));
    }

    [Theory]
    [InlineData("abcd-efgh-ijkl-mnop", true)]
    [InlineData(" ABCD-1234-ijkl-mn0p ", true)]
    [InlineData("hunter2", false)]
    [InlineData("abcd-efgh-ijkl", false)]
    [InlineData("abcd efgh ijkl mnop", false)]
    [InlineData("abcd-efgh-ijkl-mnop-qrst", false)]
    [InlineData(null, false)]
    public void OnlyAnAppPasswordIsTaken(string? value, bool taken)
    {
        Assert.Equal(taken, BlueskyText.IsAppPassword(value));
    }

    [Fact]
    public void PartsColourLinksAndTags_AndPutTheTextBackTogether()
    {
        const string text = "🎬 Movie night #film at https://example.com/x!";

        var parts = BlueskyText.Parts(text);

        Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));
        Assert.Equal(["text", "tag", "text", "link", "text"], parts.Select(p => p.Kind));
    }

    [Fact]
    public void TheCardIsForTheFirstLink_TitledByThePostOrTheLinksHost()
    {
        var titled = BlueskyPostRecord.CardFor("Movie night", "One https://www.example.com/a two https://other.example/b");
        var untitled = BlueskyPostRecord.CardFor(null, "See https://www.example.com/a");

        Assert.Equal("https://www.example.com/a", titled!.Uri);
        Assert.Equal("Movie night", titled.Title);
        Assert.Equal("example.com", untitled!.Title);
        Assert.Null(BlueskyPostRecord.CardFor("Movie night", "No link here"));
    }

    [Fact]
    public void TheRecordCarriesTheTextTheTimeTheFacetsAndTheCard()
    {
        var at = new DateTimeOffset(2026, 10, 3, 18, 30, 15, 123, TimeSpan.FromHours(2));
        var card = new BlueskyCard("https://example.com/a", "Movie night", string.Empty);
        var thumb = new JsonObject { ["$type"] = "blob", ["size"] = 1000 };

        var record = BlueskyPostRecord.Build("Movie night https://example.com/a", at, card, thumb);

        Assert.Equal("app.bsky.feed.post", record["$type"]!.GetValue<string>());
        Assert.Equal("2026-10-03T16:30:15.123Z", record["createdAt"]!.GetValue<string>());
        Assert.Single(record["facets"]!.AsArray());
        Assert.Equal("app.bsky.embed.external", record["embed"]!["$type"]!.GetValue<string>());
        Assert.Equal(1000, record["embed"]!["external"]!["thumb"]!["size"]!.GetValue<int>());
    }

    [Fact]
    public void BlueskyGetsTheTitleOnItsOwnLine_ThenTheText_OrItsOwnTextAlone()
    {
        var post = new Post { Title = "Movie night", Text = "Friday at eight.\r\nBring snacks." };
        var plain = new PostDestination { Network = PostNetworks.Bluesky };
        var own = new PostDestination { Network = PostNetworks.Bluesky, TextOverride = "  Just this. " };

        Assert.Equal("Movie night\nFriday at eight.\nBring snacks.", PostTexts.Bluesky(post, plain));
        Assert.Equal("Just this.", PostTexts.Bluesky(post, own));
        Assert.Equal("Only text", PostTexts.Bluesky(null, "Only text"));
    }

    [Fact]
    public void TheCardPictureGoesOnlyWithALink_AndOnlyWhenMadeFromThePostsPicture()
    {
        var picture = Guid.NewGuid();
        var copy = Guid.NewGuid();
        var post = new Post { PictureId = picture };
        var destination = new PostDestination
        {
            Network = PostNetworks.Bluesky,
            SitePictureId = copy,
            Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(picture)),
        };

        Assert.Equal(copy, PostTexts.BlueskyPictureFor(post, destination, "See https://example.com/a"));
        Assert.Null(PostTexts.BlueskyPictureFor(post, destination, "No link"));

        post.PictureId = Guid.NewGuid();
        Assert.Null(PostTexts.BlueskyPictureFor(post, destination, "See https://example.com/a"));
    }
}
