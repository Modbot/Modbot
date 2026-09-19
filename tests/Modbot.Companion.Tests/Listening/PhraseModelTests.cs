using System.Text.RegularExpressions;
using Modbot.Companion.Listening;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// The pinned phrase model: one address, one size, one hash, and a phrase list the engine will
/// accept.
/// </summary>
/// <remarks>
/// <para>The phrase list is the part worth checking hardest, because a piece the model's vocabulary
/// does not have is not a warning and not a refusal — the engine ends the whole process. The pieces
/// were produced from the model's own vocabulary builder and every one of them was looked up in its
/// vocabulary before being written down; what these tests can do without the file on disk is hold
/// the shape.</para>
/// </remarks>
public class PhraseModelTests
{
    private static readonly PhraseModel Model = PhraseModel.Default;

    [Fact]
    public void IsPinnedToOneAddressOneSizeAndOneHash()
    {
        Assert.Equal("https", Model.Url.Scheme);
        Assert.Equal("github.com", Model.Url.Host);
        Assert.True(Model.Size > 0);
        Assert.Matches("^[0-9a-f]{64}$", Model.Sha256);
    }

    [Fact]
    public void IsSmallEnoughThatTurningItOnIsNotAnEvening()
    {
        // The voice is about 305 MB. This is a phrase matcher rather than a recogniser, and being
        // a twentieth of the size is part of why it was chosen.
        Assert.InRange(Model.Size, 1_000_000, 50_000_000);
    }

    [Fact]
    public void ListensForModbotClipThat()
    {
        Assert.Contains("Modbot, clip that", Model.Spoken);

        // A few spellings rather than one: the engine hears sounds, "Modbot" is not a word it was
        // trained on, and "clip this" means what "clip that" means.
        Assert.InRange(Model.Phrases.Count, 2, 8);
        Assert.All(Model.Phrases, p => Assert.Contains("clip", p.Said, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryPhraseIsWrittenInPiecesTheEngineCouldHave()
    {
        // The vocabulary is upper-case letters, apostrophes and the mark that means "a word starts
        // here". Anything else in this list would be a piece the engine cannot look up, and a piece
        // it cannot look up ends the process rather than being ignored.
        foreach (var phrase in Model.Phrases)
        {
            Assert.False(string.IsNullOrWhiteSpace(phrase.Pieces));

            foreach (var piece in phrase.Pieces.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Assert.Matches(new Regex("^▁?[A-Z']+$"), piece);
        }
    }

    [Fact]
    public void EveryPhraseStartsAWordAndIsLongEnoughNotToFireByAccident()
    {
        foreach (var phrase in Model.Phrases)
        {
            var pieces = phrase.Pieces.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.StartsWith("▁", pieces[0], StringComparison.Ordinal);

            // Three words at least. A one-word phrase in a room full of people talking is a clip
            // saved every few minutes for no reason.
            Assert.True(pieces.Count(p => p.StartsWith('▁')) >= 3, phrase.Said);
        }
    }

    [Fact]
    public void ThePhrasesFileIsOnePhraseALineAndNothingElse()
    {
        var text = Model.PhrasesText();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Model.Phrases.Count, lines.Length);

        for (var i = 0; i < lines.Length; i++)
            Assert.Equal(Model.Phrases[i].Pieces, lines[i]);

        // No label after an "@": the engine takes one word there, not a sentence, and anything it
        // does not recognise ends the process rather than being ignored.
        Assert.DoesNotContain('@', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpokenListIsTheSameListEveryTime()
    {
        // The window compares one snapshot with the last to decide whether to redraw, so a fresh
        // list each time would redraw the settings page once a second forever and take whatever
        // somebody was typing with it.
        Assert.Same(Model.Spoken, Model.Spoken);
    }

    [Fact]
    public void TheFolderAndItsFilesAreUnderModbotsOwnFolder()
    {
        var folder = PhraseModel.PhrasesFolder(Path.Combine("X:", "Modbot"));

        Assert.EndsWith("phrases", folder, StringComparison.Ordinal);

        foreach (var path in new[]
                 {
                     Model.EncoderPath(folder), Model.DecoderPath(folder), Model.JoinerPath(folder),
                     Model.TokensPath(folder), Model.PhrasesPath(folder), Model.MarkerPath(folder),
                 })
        {
            Assert.StartsWith(Model.Folder(folder), path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFolderWithNothingInItIsNotPresent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "modbot-phrase-model-tests", Guid.NewGuid().ToString("n"));

        Assert.False(Model.IsPresent(folder));
    }

    [Fact]
    public void TheMarkerNamesWhatWasFetchedAndWhatItListensFor()
    {
        var marker = Model.MarkerText();

        Assert.Contains(Model.Sha256, marker, StringComparison.Ordinal);
        Assert.Contains(Model.Url.ToString(), marker, StringComparison.Ordinal);

        // The phrases go in the marker too, so a client whose phrase list has changed rewrites the
        // file rather than listening for the old list forever.
        Assert.Contains("phrases", marker, StringComparison.Ordinal);
    }
}
