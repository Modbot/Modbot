using System.Text.Json.Nodes;
using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Tests.Bluesky;

/// <summary>Who may reply to a post, quote posts and languages: the records Bluesky keeps them in.</summary>
public class BlueskyPostGatesTests
{
    private const string PostUri = "at://did:plc:abc/app.bsky.feed.post/3l4pqjzzyde2a";
    private static readonly DateTimeOffset At = new(2026, 10, 16, 20, 0, 0, TimeSpan.Zero);

    private static string[] RulesOf(JsonObject gate) =>
        [.. gate["allow"]!.AsArray().Select(r => r!["$type"]!.GetValue<string>())];

    [Fact]
    public void AnyoneMakesNoThreadgate()
    {
        Assert.Null(BlueskyPostGates.Threadgate(PostUri, null, At));
    }

    [Fact]
    public void NobodyIsAnEmptyListOfRules()
    {
        var gate = BlueskyPostGates.Threadgate(PostUri, [BlueskyPostGates.Nobody], At)!;

        Assert.Equal("app.bsky.feed.threadgate", gate["$type"]!.GetValue<string>());
        Assert.Equal(PostUri, gate["post"]!.GetValue<string>());
        Assert.Empty(gate["allow"]!.AsArray());
        Assert.Equal("2026-10-16T20:00:00.000Z", gate["createdAt"]!.GetValue<string>());
    }

    [Fact]
    public void EachGroupIsItsOwnRule_AndAMixHasEveryOneChosen()
    {
        Assert.Equal(["app.bsky.feed.threadgate#followerRule"], RulesOf(BlueskyPostGates.Threadgate(PostUri, ["followers"], At)!));
        Assert.Equal(["app.bsky.feed.threadgate#followingRule"], RulesOf(BlueskyPostGates.Threadgate(PostUri, ["following"], At)!));
        Assert.Equal(["app.bsky.feed.threadgate#mentionRule"], RulesOf(BlueskyPostGates.Threadgate(PostUri, ["mentioned"], At)!));

        Assert.Equal(
            ["app.bsky.feed.threadgate#mentionRule", "app.bsky.feed.threadgate#followerRule", "app.bsky.feed.threadgate#followingRule"],
            RulesOf(BlueskyPostGates.Threadgate(PostUri, ["followers", "following", "mentioned"], At)!));
    }

    [Fact]
    public void QuotesOffMakesAPostgateThatDisablesEmbedding_QuotesOnMakesNone()
    {
        Assert.Null(BlueskyPostGates.Postgate(PostUri, quotesOff: false, At));

        var gate = BlueskyPostGates.Postgate(PostUri, quotesOff: true, At)!;

        Assert.Equal("app.bsky.feed.postgate", gate["$type"]!.GetValue<string>());
        Assert.Equal(PostUri, gate["post"]!.GetValue<string>());
        Assert.Equal("app.bsky.feed.postgate#disableRule", gate["embeddingRules"]![0]!["$type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("anyone", null)]
    [InlineData("nobody", "nobody")]
    [InlineData("mentioned,followers", "followers,mentioned")]
    [InlineData("following,following", "following")]
    public void AChoiceIsKeptOnceEach_InTheFormsOrder_AndAnyoneIsNothing(string? words, string? kept)
    {
        var choice = words?.Split(',');

        Assert.Equal(kept, BlueskyPostGates.Normalize(choice) is { } list ? string.Join(',', list) : null);
    }

    [Theory]
    [InlineData("anyone", true)]
    [InlineData("nobody", true)]
    [InlineData("followers,following,mentioned", true)]
    [InlineData("anyone,followers", false)]
    [InlineData("nobody,followers", false)]
    [InlineData("everyone", false)]
    public void OnlyBlueskysOwnChoicesAreChoices(string words, bool choice)
        => Assert.Equal(choice, BlueskyPostGates.IsChoice(words.Split(',')));

    [Fact]
    public void ThePostCarriesItsLanguagesAsLangs_AndNoneLeavesTheFieldOut()
    {
        var with = BlueskyPostRecord.Build("Hello", At, null, langs: ["en", "pt-BR"]);
        var without = BlueskyPostRecord.Build("Hello", At, null, langs: []);

        Assert.Equal(["en", "pt-BR"], with["langs"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.False(without.ContainsKey("langs"));
        Assert.False(BlueskyPostRecord.Build("Hello", At, null).ContainsKey("langs"));
    }

    [Fact]
    public void AtMostThreeLanguagesGoOnThePost()
    {
        var record = BlueskyPostRecord.Build("Hello", At, null, langs: ["en", "de", "fr", "es"]);

        Assert.Equal(3, record["langs"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("fil", true)]
    [InlineData("", false)]
    [InlineData("e", false)]
    [InlineData("english", false)]
    [InlineData("en_US", false)]
    public void ALanguageCodeIsTwoOrThreeLettersWithOptionalParts(string code, bool isCode)
        => Assert.Equal(isCode,BlueskyLanguages.IsCode(code));

    [Fact]
    public void LanguagesAreTrimmedWithoutBlanksOrRepeats_AndNoneIsNull()
    {
        Assert.Equal(["en", "de"], BlueskyLanguages.Clean([" en ", "", "EN", "de"]));
        Assert.Null(BlueskyLanguages.Clean([" "]));
        Assert.Null(BlueskyLanguages.Clean(null));
    }

    [Theory]
    [InlineData("atproto transition:generic", true)]
    [InlineData("atproto repo:app.bsky.feed.post?action=create&action=update&action=delete", false)]
    [InlineData("atproto repo:app.bsky.feed.threadgate?action=create repo:app.bsky.feed.postgate?action=create", true)]
    [InlineData("atproto repo:app.bsky.feed.threadgate?action=create", false)]
    [InlineData(null, false)]
    public void ASignInMakesGatesOnlyWithBothScopesOrTheWideOne(string? scope, bool can)
        => Assert.Equal(can, BlueskyOAuth.CanSetWhoCanReply(scope));

    [Fact]
    public void TheNarrowScopeCanMakeGates()
        => Assert.True(BlueskyOAuth.CanSetWhoCanReply(BlueskyOAuth.NarrowScope));

    [Fact]
    public void OptionsWithNoChoiceReadFromOlderJsonAndHaveNoGates()
    {
        var options = PostTexts.ParseBlueskyOptions("{\"cid\":\"bafy\",\"fingerprint\":\"abc\"}");

        Assert.False(options.HasGates);
        Assert.Null(options.WhoCanReply);
        Assert.Null(options.Languages);
        Assert.False(options.SettingsToSend);
    }

    [Fact]
    public void TheChoicesSurviveTheOptionsJson()
    {
        var written = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(
            WhoCanReply: ["followers"], QuotesOff: true, Languages: ["en"], SettingsToSend: true));

        Assert.DoesNotContain("hasGates", written, StringComparison.OrdinalIgnoreCase);

        var read = PostTexts.ParseBlueskyOptions(written);
        Assert.True(read.HasGates);
        Assert.Equal(["followers"], read.WhoCanReply);
        Assert.True(read.QuotesOff);
        Assert.Equal(["en"], read.Languages);
        Assert.True(read.SettingsToSend);
    }

    [Fact]
    public void NoChoiceGivesTheFingerprintFromBeforeTheChoicesExisted()
    {
        var picture = Guid.CreateVersion7();
        List<BlueskyPostPicture> pictures = [new(picture, "x")];

        Assert.Equal(CalendarFingerprint.Of("one", picture), CalendarBlueskyText.Fingerprint("one", picture, null, null, false, null));
        Assert.Equal(CalendarBlueskyText.Fingerprint("one", picture, pictures), CalendarBlueskyText.Fingerprint("one", picture, pictures, null, false, []));
    }

    [Fact]
    public void EachChoiceChangesTheFingerprint()
    {
        var plain = CalendarBlueskyText.Fingerprint("one", null);

        var nobody = CalendarBlueskyText.Fingerprint("one", null, null, [BlueskyPostGates.Nobody]);
        var followers = CalendarBlueskyText.Fingerprint("one", null, null, [BlueskyPostGates.Followers]);
        var noQuotes = CalendarBlueskyText.Fingerprint("one", null, null, null, quotesOff: true);
        var english = CalendarBlueskyText.Fingerprint("one", null, null, null, false, ["en"]);
        var german = CalendarBlueskyText.Fingerprint("one", null, null, null, false, ["de"]);

        Assert.Equal(6, new[] { plain, nobody, followers, noQuotes, english, german }.Distinct().Count());
    }
}
