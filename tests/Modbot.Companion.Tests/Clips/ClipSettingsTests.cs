using Modbot.Companion.Clips;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Clips;

/// <summary>
/// The Clips settings: off unless somebody turned them on, a length that cannot be talked out of
/// its range, and a round trip through <c>settings.json</c> that leaves every other field alone.
/// </summary>
public class ClipSettingsTests
{
    [Fact]
    public void KeepingTheLastFewMinutesIsOffUntilSomebodyTurnsItOn()
    {
        // The whole of the consent. A client updated into a version that can record must not start
        // recording, so "missing" and "off" have to be the same answer.
        Assert.False(ClipSettings.Default.On);
        Assert.False(new ClipSettings().On);
    }

    [Fact]
    public void AFileWithNoClipsObjectLeavesItOff()
    {
        var path = TempFile("""{ "startWithWindows": false }""");

        var settings = CompanionSettings.Load(path, _ => null);

        Assert.False(settings.Clips.On);
        Assert.Equal(ClipSettings.DefaultMinutes, settings.Clips.Minutes);
        Assert.Null(settings.Clips.Folder);
    }

    [Theory]
    [InlineData(0, ClipSettings.MinMinutes)]
    [InlineData(1, ClipSettings.MinMinutes)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, ClipSettings.MaxMinutes)]
    [InlineData(90, ClipSettings.MaxMinutes)]
    [InlineData(-4, ClipSettings.MinMinutes)]
    public void TheLengthIsHeldBetweenTwoAndFiveMinutes(int asked, int expected)
        => Assert.Equal(expected, ClipSettings.ClampMinutes(asked));

    [Fact]
    public void AHandEditedFileAskingForNinetyMinutesGetsFive()
    {
        // The reason the clamp exists: the file is meant to be hand-editable, and an hour and a
        // half of video on somebody's disk is not a thing a typo should be able to cause.
        var path = TempFile("""{ "clips": { "on": true, "minutes": 90 } }""");

        Assert.Equal(ClipSettings.MaxMinutes, CompanionSettings.Load(path, _ => null).Clips.Minutes);
    }

    [Fact]
    public void TheRoomSavedClipsMayTakeIsHeldInRangeToo()
    {
        Assert.Equal(ClipSettings.MinKeepGigabytes, ClipSettings.ClampKeepGigabytes(0));
        Assert.Equal(ClipSettings.MaxKeepGigabytes, ClipSettings.ClampKeepGigabytes(100_000));
        Assert.Equal(20, ClipSettings.ClampKeepGigabytes(20));
    }

    [Fact]
    public void SavingTheClipsCardKeepsEveryOtherFieldInTheFile()
    {
        // The same rule as SaveVoice and SaveNotifications: one object is rewritten and the rest of
        // a hand-edited file is left exactly as it was.
        var path = TempFile("""
            {
              "pairingPage": "https://example.test/pair",
              "startWithWindows": false,
              "cloud": { "disabled": true }
            }
            """);

        Assert.True(CompanionSettings.SaveClips(path, new ClipSettings(true, 4, @"D:\Clips", 12)));

        var reloaded = CompanionSettings.Load(path, _ => null);

        Assert.True(reloaded.Clips.On);
        Assert.Equal(4, reloaded.Clips.Minutes);
        Assert.Equal(@"D:\Clips", reloaded.Clips.Folder);
        Assert.Equal(12, reloaded.Clips.KeepGigabytes);

        Assert.False(reloaded.StartWithWindows);
        Assert.Equal("https://example.test/pair", reloaded.PairingPage.ToString());
        Assert.True(reloaded.Cloud.Disabled);
    }

    [Fact]
    public void TheUsualFolderIsNotWrittenIntoTheFile()
    {
        // A path pinned into the file would stop following the machine — a profile moved to another
        // drive, or a Windows in another language — so "the usual place" is said by saying nothing.
        var path = TempFile("{}");

        Assert.True(CompanionSettings.SaveClips(path, new ClipSettings(true, 3)));

        Assert.DoesNotContain("folder", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(CompanionSettings.Load(path, _ => null).Clips.Folder);
    }

    [Fact]
    public void AFileThatIsNotJsonIsLeftAloneRatherThanOverwritten()
    {
        var path = TempFile("this is not json");

        Assert.False(CompanionSettings.SaveClips(path, new ClipSettings(true, 3)));
        Assert.Equal("this is not json", File.ReadAllText(path));
    }

    [Fact]
    public void DiscordsSoundIsOffUntilSomebodyTurnsItOn()
    {
        // VRChat's sound is what Clips means and has no field. Discord's is a second program and a
        // second set of people, so it is its own switch — and a client updated into a version that
        // can record Discord must not start recording Discord.
        Assert.False(ClipSettings.Default.DiscordSound);
        Assert.False(new ClipSettings().DiscordSound);
        Assert.False(new ClipSettings(On: true).DiscordSound);
    }

    [Fact]
    public void AFileWithNoDiscordSoundFieldLeavesItOff()
    {
        var path = TempFile("""{ "clips": { "on": true, "minutes": 4 } }""");

        var clips = CompanionSettings.Load(path, _ => null).Clips;

        Assert.True(clips.On);
        Assert.False(clips.DiscordSound);
    }

    [Fact]
    public void DiscordsSoundSurvivesARoundTripThroughTheFile()
    {
        var path = TempFile("{}");

        Assert.True(CompanionSettings.SaveClips(path, new ClipSettings(true, 3, null, 5, DiscordSound: true)));

        Assert.True(CompanionSettings.Load(path, _ => null).Clips.DiscordSound);
    }

    [Fact]
    public void ADiscordSoundThatIsOffIsStillWrittenIntoTheFile()
    {
        // Unlike the folder, which says "the usual place" by saying nothing. Somebody who opens
        // this file to find out whether Modbot is recording Discord should read the answer rather
        // than have to know what a missing field means.
        var path = TempFile("{}");

        Assert.True(CompanionSettings.SaveClips(path, new ClipSettings(true, 3)));

        Assert.Contains("\"discordSound\": false", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ClampingBringsEveryNumberInside()
    {
        var clamped = new ClipSettings(true, 99, "   ", 9999).Clamped();

        Assert.Equal(ClipSettings.MaxMinutes, clamped.Minutes);
        Assert.Null(clamped.Folder);
        Assert.Equal(ClipSettings.MaxKeepGigabytes, clamped.KeepGigabytes);
    }

    private static string TempFile(string contents)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "modbot-clip-settings-tests",
            Guid.NewGuid().ToString("n"),
            "settings.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
}
