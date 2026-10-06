using System.Reflection;
using Modbot.Companion.Presentation;
using Xunit;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// When the desktop overlay's window comes up after the shortcut: at once, or hidden for a moment
/// while VRChat's palette is looked for again. The window waits only while the client does not yet
/// know who the moderator is, and never for longer than <see cref="OverlayOpening.LongestWait"/>.
/// </summary>
public sealed class OverlayOpeningTests
{
    private static readonly TimeSpan Start = TimeSpan.Zero;

    [Fact]
    public void TheWaitIsOneAndAHalfSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(1.5), OverlayOpening.LongestWait);

    [Fact]
    public void TheWaitLooksAgainSeveralTimesBeforeItRunsOut()
        => Assert.InRange(OverlayOpening.LongestWait / OverlayOpening.LookEvery, 8, 20);

    [Fact]
    public void AFoundPaletteOpensTheWindowAtOnce()
        => Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(paletteFound: true, userKnown: true, vrchatIsUp: true, Start));

    [Fact]
    public void AFoundPaletteOpensTheWindowAtOnceEvenWhenNothingElseIsKnown()
        => Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(paletteFound: true, userKnown: false, vrchatIsUp: false, Start));

    [Fact]
    public void NoUserYetWithVRChatUpWaitsAndLooksAgain()
        => Assert.Equal(
            OverlayOpeningStep.WaitAndLookAgain,
            OverlayOpening.Next(paletteFound: false, userKnown: false, vrchatIsUp: true, Start));

    [Theory]
    [InlineData(125)]
    [InlineData(750)]
    [InlineData(1499)]
    public void NoUserYetKeepsWaitingUntilTheLimit(int milliseconds)
        => Assert.Equal(
            OverlayOpeningStep.WaitAndLookAgain,
            OverlayOpening.Next(false, false, true, TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData(1500)]
    [InlineData(1501)]
    [InlineData(10_000)]
    public void NoUserYetOpensTheWindowInTheUsualLookWhenTheLimitIsReached(int milliseconds)
        => Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(false, false, true, TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void AKnownUserWithNoPaletteSelectedOpensTheWindowAtOnce()
        => Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(paletteFound: false, userKnown: true, vrchatIsUp: true, Start));

    [Fact]
    public void WithVRChatNotRunningTheWindowOpensAtOnce()
        => Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(paletteFound: false, userKnown: false, vrchatIsUp: false, Start));

    [Fact]
    public void AWaitingWindowOpensAtOnceWhenVRChatClosesUnderIt()
    {
        Assert.Equal(
            OverlayOpeningStep.WaitAndLookAgain,
            OverlayOpening.Next(false, false, true, TimeSpan.FromMilliseconds(500)));

        Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(false, false, false, TimeSpan.FromMilliseconds(625)));
    }

    [Fact]
    public void AWaitingWindowOpensAtOnceWhenThePaletteTurnsUp()
    {
        Assert.Equal(
            OverlayOpeningStep.WaitAndLookAgain,
            OverlayOpening.Next(false, false, true, TimeSpan.FromMilliseconds(250)));

        Assert.Equal(
            OverlayOpeningStep.ShowNow,
            OverlayOpening.Next(true, true, true, TimeSpan.FromMilliseconds(375)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1499, false)]
    [InlineData(1500, true)]
    [InlineData(3000, true)]
    public void TheWaitIsOverAtTheLimit(int milliseconds, bool over)
        => Assert.Equal(over, OverlayOpening.IsOver(TimeSpan.FromMilliseconds(milliseconds)));

    /// <summary>
    /// Modbot's bubble in VRChat's Esc menu row has its own fixed look and no palette to load, so
    /// it must come up at once and never through this wait. The three files that make it are held
    /// to knowing nothing of the wait, of the desktop overlay's window, or of VRChat's palette.
    /// </summary>
    [Theory]
    [InlineData("Modbot.Companion", "Presentation", "EscapeBubblePlan.cs")]
    [InlineData("Modbot.Companion.App", "", "EscapeBubbleHost.cs")]
    [InlineData("Modbot.Companion.App", "", "EscapeBubbleWindow.cs")]
    public void TheEscapeBubbleDoesNotDependOnTheWaitOrOnVRChatsPalette(string project, string folder, string file)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", project, folder, file));

        foreach (var name in new[]
        {
            nameof(OverlayOpening),
            nameof(OverlayOpeningStep),
            "DesktopOverlayWindow",
            "Summon(",
            "VRChatPalette",
            "PaletteRead",
            "LookAtPalette",
        })
        {
            Assert.DoesNotContain(name, source, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Modbot.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
