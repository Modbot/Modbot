using System.Text.RegularExpressions;
using Modbot.Companion.Listening;

namespace Modbot.Companion.Tests.Listening;

/// <summary>
/// The pinned phrase model: one address, one size, one hash, the client's own name, and the list of
/// things it can be asked to do once that name has been heard.
/// </summary>
/// <remarks>
/// <para>The two lists are the part worth checking hardest, because a piece the model's vocabulary
/// does not have is not a warning and not a refusal — the engine ends the whole process. Every
/// piece was produced by the model's own tokenizer and looked up in its vocabulary before being
/// written down; what these tests can do without the file on disk is hold the shape.</para>
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
    public void ItListensForBothSpellingsOfItsOwnName()
    {
        // The engine hears sounds rather than spellings, and "Modbot" is not a word it was trained
        // on: it can come out as one run of pieces or as "mod" and "bot" separately. Both are in
        // the list, and both arm the client.
        Assert.Equal("Modbot", Model.Called);
        Assert.Equal(["Modbot", "Mod bot"], Model.Names.Select(n => n.Said));

        Assert.True(Model.IsTheName("▁MO D B O T"));
        Assert.True(Model.IsTheName("▁MO D ▁BO T"));

        // The engine may answer with the pieces or with the words those pieces spell.
        Assert.True(Model.IsTheName("MODBOT"));
        Assert.True(Model.IsTheName("MOD BOT"));

        Assert.False(Model.IsTheName("MOD"));
        Assert.False(Model.IsTheName("SHOW OVERLAY"));
        Assert.False(Model.IsTheName(""));
        Assert.False(Model.IsTheName(null));
    }

    [Fact]
    public void ItCanBeAskedToSaveAClipAndToShowAndHideTheOverlay()
    {
        Assert.Equal(
            ["clip that", "clip this", "show overlay", "hide overlay"],
            Model.Commands.Select(c => c.Said));

        Assert.Equal(WhatToDo.ShowOverlay, Model.CommandFor("▁SHOW ▁OVER LA Y")?.Does);
        Assert.Equal(WhatToDo.HideOverlay, Model.CommandFor("▁HI DE ▁OVER LA Y")?.Does);
        Assert.Equal(WhatToDo.SaveClip, Model.CommandFor("▁C LI P ▁THAT")?.Does);
        Assert.Equal(WhatToDo.SaveClip, Model.CommandFor("▁C LI P ▁THIS")?.Does);

        // Whichever way the engine spells its answer back.
        Assert.Equal(WhatToDo.ShowOverlay, Model.CommandFor("SHOW OVERLAY")?.Does);
        Assert.Equal(WhatToDo.HideOverlay, Model.CommandFor("HIDE OVERLAY")?.Does);
    }

    [Fact]
    public void AnAnswerThisClientDoesNotKnowIsDroppedRatherThanGuessedAt()
    {
        Assert.Null(Model.CommandFor("▁SHOW"));
        Assert.Null(Model.CommandFor("OVERLAY"));
        Assert.Null(Model.CommandFor("HELLO WORLD"));
        Assert.Null(Model.CommandFor(""));
        Assert.Null(Model.CommandFor(null));

        // The name is not a command. Saying it does nothing but start the waiting.
        Assert.Null(Model.CommandFor("▁MO D B O T"));
    }

    [Fact]
    public void WhatAModeratorSaysIsTheNameAndThenTheCommand()
    {
        Assert.Equal(
            [
                "Modbot, clip that",
                "Modbot, clip this",
                "Modbot, show overlay",
                "Modbot, hide overlay",
            ],
            Model.Spoken);
    }

    [Fact]
    public void EveryPhraseIsWrittenInPiecesTheEngineCouldHave()
    {
        // The vocabulary is upper-case letters, apostrophes and the mark that means "a word starts
        // here". Anything else in either list would be a piece the engine cannot look up, and a
        // piece it cannot look up ends the process rather than being ignored.
        foreach (var pieces in Model.Names.Select(n => n.Pieces).Concat(Model.Commands.Select(c => c.Pieces)))
        {
            Assert.False(string.IsNullOrWhiteSpace(pieces));

            foreach (var piece in pieces.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Assert.Matches(new Regex("^▁?[A-Z']+$"), piece);
        }
    }

    [Fact]
    public void EveryPhraseStartsAWordAndACommandIsAtLeastTwoOfThem()
    {
        foreach (var name in Model.Names)
            Assert.StartsWith("▁", name.Pieces, StringComparison.Ordinal);

        foreach (var command in Model.Commands)
        {
            var pieces = command.Pieces.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.StartsWith("▁", pieces[0], StringComparison.Ordinal);

            // Two words at least. It was three while a phrase carried the name in front of it; a
            // command is shorter now because it is only listened for in the few seconds after the
            // name was heard, and "overlay" on its own would still be a word people say.
            Assert.True(pieces.Count(p => p.StartsWith('▁')) >= 2, command.Said);
        }
    }

    [Fact]
    public void TheNameIsNeverAlsoACommand()
    {
        // The two lists go to two streams of the same matcher. A word in both would mean the name
        // arming the client and then immediately being taken as the instruction.
        foreach (var name in Model.Names)
            Assert.Null(Model.CommandFor(name.Pieces));
    }

    [Fact]
    public void TheTwoFilesAreOnePhraseALineAndNothingElse()
    {
        foreach (var (text, count) in new[]
                 {
                     (Model.NamesText(), Model.Names.Count),
                     (Model.CommandsText(), Model.Commands.Count),
                 })
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(count, lines.Length);

            // No label after an "@": the engine takes one word there, not a sentence, and anything
            // it does not recognise ends the process rather than being ignored.
            Assert.DoesNotContain('@', text);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
        }

        Assert.Equal([.. Model.Names.Select(n => n.Pieces)], Model.NamesText().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal([.. Model.Commands.Select(c => c.Pieces)], Model.CommandsText().Split('\n', StringSplitOptions.RemoveEmptyEntries));
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
                     Model.TokensPath(folder), Model.NamesPath(folder), Model.CommandsPath(folder),
                     Model.MarkerPath(folder),
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
    public void TheMarkerNamesWhatWasFetchedAndBothListsItListensFor()
    {
        var marker = Model.MarkerText();

        Assert.Contains(Model.Sha256, marker, StringComparison.Ordinal);
        Assert.Contains(Model.Url.ToString(), marker, StringComparison.Ordinal);

        // Both lists go in the marker, so a client whose name or commands have changed rewrites the
        // folder rather than listening for the old lists forever.
        Assert.Contains("listensFor", marker, StringComparison.Ordinal);
        Assert.Contains("commands", marker, StringComparison.Ordinal);
    }
}
