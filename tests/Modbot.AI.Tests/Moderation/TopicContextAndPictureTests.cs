using System.ClientModel.Primitives;
using Modbot.AI.Moderation;
using Modbot.Core.Moderation;
using OpenAI.Chat;

namespace Modbot.AI.Tests.Moderation;

/// <summary>
/// The messages around the one being checked (AI moderation design §16) and the pictures that go
/// with it (§17): what the request carries, and what is refused when the answer comes back.
/// </summary>
public class TopicContextAndPictureTests
{
    private static readonly TopicToCheck Insults =
        new("t1", Guid.NewGuid(), "Insults", "Calling another member names.", "medium");

    private static readonly ContextMessage[] Conversation =
    [
        new("m1", "Alice", "look at the avatar I just bought", ReplyTarget: true),
        new("m2", "Bob", "that shop is a scam by the way", ReplyTarget: false),
    ];

    private static readonly PictureToCheck[] Pictures =
    [
        new("p1", "Attachment cat.png", "https://cdn.example/cat.png", null, null),
        new("p2", "Discord avatar", "https://cdn.example/avatar.png", BinaryData.FromBytes([1, 2, 3]), "image/png"),
    ];

    [Fact]
    public void TheEarlierMessagesGoInsideTheMarkers_MarkedAsContext_AndOnlyTheLastIsJudged()
    {
        var marker = TopicClassifier.NewMarker();
        var text = new TopicText("x1", ModerationTargets.DiscordMessage, "you are such a clown", [Insults], Conversation);

        var prompt = TopicClassifier.Prompt([text], marker);
        var content = Assert.Single(prompt.Contents).ReplaceLineEndings("\n");

        // Nothing a member wrote is in the instructions, the context included.
        Assert.DoesNotContain("clown", prompt.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("scam", prompt.Instructions, StringComparison.Ordinal);
        Assert.Contains("Judge the last one alone", prompt.Instructions, StringComparison.Ordinal);

        // The context is inside the same markers, and is labelled as context.
        Assert.StartsWith($"{marker} x1\n", content, StringComparison.Ordinal);
        Assert.EndsWith(marker, content, StringComparison.Ordinal);
        Assert.Contains("Earlier messages, for context only, never judged:", content, StringComparison.Ordinal);
        Assert.Contains("- Alice (the message this replies to): look at the avatar I just bought", content, StringComparison.Ordinal);
        Assert.Contains("- Bob: that shop is a scam by the way", content, StringComparison.Ordinal);
        Assert.Contains("The message to judge:\nyou are such a clown", content, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoContextTheRequestIsExactlyWhatItAlwaysWas()
    {
        var marker = TopicClassifier.NewMarker();
        var text = new TopicText("x1", ModerationTargets.DiscordMessage, "you are such a clown", [Insults]);

        var prompt = TopicClassifier.Prompt([text], marker);

        Assert.Equal(
            $"{marker} x1\nyou are such a clown\n{marker}",
            Assert.Single(prompt.Contents).ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Earlier messages", prompt.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteTakenFromTheContextIsRefused()
    {
        // Bob really did write it, one message earlier. It is still not what this member said, so
        // the hit is thrown away exactly as an invented quote is (§16.2).
        var text = new TopicText("x1", ModerationTargets.DiscordMessage, "you are such a clown", [Insults], Conversation);

        var check = TopicClassifier.Read(
            """{"matches":[{"text":"x1","topic":"t1","why":"Called the shop a scam.","quote":"that shop is a scam"}]}""",
            [text]);

        Assert.Null(check.Error);
        Assert.Empty(check.Hits);
    }

    [Fact]
    public void APictureGoesBehindItsKeyInsideTheMarkers()
    {
        var marker = TopicClassifier.NewMarker();
        var text = new TopicText(
            "x1", ModerationTargets.DiscordMessage, "look at this", [Insults], Context: null, Pictures);

        var prompt = TopicClassifier.Prompt([text], marker);

        // Only Modbot's own keys reach the instructions: a file name is written by a member.
        Assert.Contains("x1 also carries pictures, each after its key: p1, p2.", prompt.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("cat.png", prompt.Instructions, StringComparison.Ordinal);

        var message = Assert.IsType<UserChatMessage>(Assert.Single(TopicClassifier.ContentMessages(prompt, [text])));
        var parts = message.Content;

        Assert.Equal(6, parts.Count);
        Assert.Contains("The pictures to judge, each after its key:", parts[0].Text, StringComparison.Ordinal);
        Assert.Equal("p1:", parts[1].Text);
        Assert.Equal("https://cdn.example/cat.png", parts[2].ImageUri!.ToString());
        Assert.Equal("p2:", parts[3].Text);
        Assert.NotNull(parts[4].ImageBytes);

        // The pictures sit between the markers with the words, not after them.
        Assert.Equal(marker, parts[5].Text);
    }

    [Fact]
    public void APictureCheckAsksForTheSchemaWithAPictureField()
    {
        var withPictures = new TopicText(
            "x1", ModerationTargets.DiscordMessage, "look at this", [Insults], Context: null, Pictures);
        var withoutPictures = new TopicText("x1", ModerationTargets.DiscordMessage, "look at this", [Insults]);

        Assert.True(TopicClassifier.AnyPictures([withPictures]));
        Assert.False(TopicClassifier.AnyPictures([withoutPictures]));

        var options = TopicClassifier.Options("custom", 1, withPictures: true);
        var schema = ModelReaderWriter.Write(options.ResponseFormat).ToString();
        Assert.Contains("picture", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnswerSaysWhichPictureMatched()
    {
        var text = new TopicText(
            "x1", ModerationTargets.DiscordMessage, "look at this", [Insults], Context: null, Pictures);

        var check = TopicClassifier.Read(
            """{"matches":[{"text":"x1","topic":"t1","why":"The picture is a slur.","quote":"","picture":"p2"}]}""",
            [text]);

        var hit = Assert.Single(check.Hits);
        Assert.Equal("Discord avatar", hit.Picture!.Label);
        Assert.Equal("Discord avatar", hit.Quote);
    }

    [Fact]
    public void APictureNobodySentIsInventedAndThrownAway()
    {
        var text = new TopicText(
            "x1", ModerationTargets.DiscordMessage, "look at this", [Insults], Context: null, Pictures);

        var check = TopicClassifier.Read(
            """{"matches":[{"text":"x1","topic":"t1","why":"x","quote":"","picture":"p7"}]}""",
            [text]);

        Assert.Null(check.Error);
        Assert.Empty(check.Hits);
    }

    [Fact]
    public void WithPicturesSentAMatchMayStillBeInTheWords()
    {
        var text = new TopicText(
            "x1", ModerationTargets.DiscordMessage, "you are such a clown", [Insults], Context: null, Pictures);

        var check = TopicClassifier.Read(
            """{"matches":[{"text":"x1","topic":"t1","why":"Called them a clown.","quote":"such a clown","picture":""}]}""",
            [text]);

        var hit = Assert.Single(check.Hits);
        Assert.Null(hit.Picture);
        Assert.Equal("such a clown", hit.Quote);
    }

    [Fact]
    public void AnAnswerMissingThePictureFieldIsThrownAwayWhole()
    {
        var text = new TopicText(
            "x1", ModerationTargets.DiscordMessage, "you are such a clown", [Insults], Context: null, Pictures);

        var check = TopicClassifier.Read(
            """{"matches":[{"text":"x1","topic":"t1","why":"x","quote":"such a clown"}]}""",
            [text]);

        Assert.Empty(check.Hits);
        Assert.NotNull(check.Error);
    }
}
