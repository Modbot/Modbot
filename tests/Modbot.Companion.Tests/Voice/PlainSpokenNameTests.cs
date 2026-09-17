using Modbot.Companion.Voice;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// The stand-in name normaliser: light on purpose. It takes decoration off and guesses at
/// nothing; the real one plugs into <see cref="ISpokenName"/> later.
/// </summary>
public class PlainSpokenNameTests
{
    private readonly PlainSpokenName _names = new();

    [Theory]
    [InlineData("Rin", "Rin")]
    [InlineData("Rin Kai", "Rin Kai")]
    [InlineData("Ŕïņ", "Rin")]
    [InlineData("Rì́̂̃n", "Rin")]
    [InlineData("R​i‌n‍﻿", "Rin")]
    [InlineData("‮Rin‬", "Rin")]
    [InlineData("  Rin \t\n  Kai ", "Rin Kai")]
    [InlineData("Rin　Kai", "Rin Kai")]
    public void StripsMarksAndInvisibleCharactersAndCollapsesSpaces(string name, string spoken)
    {
        Assert.Equal(spoken, _names.Spoken(name));
    }

    [Theory]
    [InlineData("Rin ★")]
    [InlineData("Rín")]
    [InlineData("José")]
    [InlineData("Rin_Kai")]
    [InlineData("Rin (VR)")]
    [InlineData("ᴿⁱⁿ")]
    [InlineData("リン")]
    public void LeavesEverythingElseAlone(string name)
    {
        // Look-alike letters and stylised alphabets are the real normaliser's job. A wrong guess
        // said out loud is worse than a name said plainly. An accented letter written as one
        // character is a letter, and stays one.
        Assert.Equal(name, _names.Spoken(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("́̂")]
    [InlineData("​‍")]
    public void NothingSpeakableBecomesSomeone(string? name)
    {
        Assert.Equal(PlainSpokenName.Nobody, _names.Spoken(name));
    }
}
