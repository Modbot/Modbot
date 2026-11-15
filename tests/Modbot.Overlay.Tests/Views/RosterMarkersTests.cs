using Avalonia.Controls;
using Avalonia.LogicalTree;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// The two marks a roster row can carry beside the name: "Not in group" on somebody the server says is
/// not a member (whatever else their one standing word says), and "New" on somebody who joined in the
/// last two minutes. The same words in the headset's look and the desktop window's.
/// </summary>
public class RosterMarkersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 21, 0, 0, TimeSpan.Zero);

    private static RosterMember Person(string name, RosterStanding standing, bool? isMember)
        => new("usr_" + name, name, standing, 0, [], IsMember: isMember);

    private static OverlayScreen Instance(
        IReadOnlyList<RosterMember> people,
        Dictionary<string, DateTimeOffset?>? arrivals = null,
        string? moderatorId = null,
        DateTimeOffset? now = null)
        => new(
            "Cat Lounge",
            new Cached<InstanceContext>(new InstanceContext("39911", people), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Arrivals: arrivals,
            Now: now ?? Now,
            ModeratorId: moderatorId);

    private static OverlayLook VRChatLook()
    {
        static PaletteColour c(byte r, byte g, byte b) => new(r, g, b);

        return OverlayLook.FromColours(new OverlayColours(
            Panel: c(0x18, 0x12, 0x24),
            Bar: c(0x10, 0x0C, 0x1A),
            Button: c(0x2A, 0x20, 0x44),
            Border: c(0x38, 0x2C, 0x5A),
            Hover: c(0x30, 0x26, 0x4E),
            Selected: c(0x50, 0x3A, 0x90),
            SelectedBorder: c(0x80, 0x60, 0xE0),
            SelectedText: c(0xF5, 0xF5, 0xF5),
            Icon: c(0xA0, 0x90, 0xD0),
            Text: c(0xF0, 0xF0, 0xF8),
            Subtext: c(0xB0, 0xA8, 0xC8)));
    }

    private static IReadOnlyList<string?> Texts(OverlayScreen screen, bool inVRChatLook)
        => AvaloniaTestHost.Run<IReadOnlyList<string?>>(() =>
            [.. OverlayView.Build(screen, null, inVRChatLook ? VRChatLook() : OverlayLook.Headset)
                .GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text)]);

    private static int Count(IReadOnlyList<string?> texts, string word) => texts.Count(t => t == word);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyThoseTheServerSaysAreNotMembersCarryTheMark(bool inVRChatLook)
    {
        // A member, a staff member and a flagged member are in the group; the standing word hides that for the last two.
        var texts = Texts(
            Instance(
            [
                Person("Mira", RosterStanding.Member, true),
                Person("Kai", RosterStanding.Staff, true),
                Person("Rin", RosterStanding.Flagged, true),
                Person("Jo", RosterStanding.Ordinary, false),
                Person("Ash", RosterStanding.Flagged, false),
            ]),
            inVRChatLook);

        Assert.Equal(2, Count(texts, "Not in group"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AServerThatSaysNothingOfMembershipGetsNoMark(bool inVRChatLook)
    {
        var texts = Texts(Instance([Person("Jo", RosterStanding.Ordinary, null)]), inVRChatLook);

        Assert.Equal(0, Count(texts, "Not in group"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SomebodyWhoJoinedInTheLastTwoMinutesIsNewAndAnEarlierArrivalIsNot(bool inVRChatLook)
    {
        var texts = Texts(
            Instance(
                [Person("Jo", RosterStanding.Member, true), Person("Ash", RosterStanding.Member, true), Person("Kai", RosterStanding.Member, true)],
                new Dictionary<string, DateTimeOffset?>
                {
                    ["usr_Jo"] = Now.AddSeconds(-90),
                    ["usr_Ash"] = Now.AddMinutes(-3),

                    // Kai was here before the moderator arrived.
                    ["usr_Kai"] = null,
                }),
            inVRChatLook);

        Assert.Equal(1, Count(texts, "New"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheModeratorIsNeverNewToThemself(bool inVRChatLook)
    {
        var texts = Texts(
            Instance(
                [Person("Me", RosterStanding.Member, true), Person("Jo", RosterStanding.Member, true)],
                new Dictionary<string, DateTimeOffset?> { ["usr_Me"] = Now.AddSeconds(-10), ["usr_Jo"] = Now.AddSeconds(-10) },
                moderatorId: "usr_Me"),
            inVRChatLook);

        Assert.Equal(1, Count(texts, "New"));
    }

    [Fact]
    public void ARowStopsBeingNewAfterTwoMinutesWithNoNewDataAndNotBefore()
    {
        var people = new[] { Person("Jo", RosterStanding.Member, true) };
        var arrivals = new Dictionary<string, DateTimeOffset?> { ["usr_Jo"] = Now.AddSeconds(-100) };

        var before = Instance(people, arrivals);

        Assert.True(before.LooksTheSameAs(Instance(people, arrivals, now: Now.AddSeconds(10))));
        Assert.False(before.LooksTheSameAs(Instance(people, arrivals, now: Now.AddSeconds(25))));

        Assert.Equal(1, Count(Texts(before, false), "New"));
        Assert.Equal(0, Count(Texts(Instance(people, arrivals, now: Now.AddSeconds(25)), false), "New"));
    }

    [Fact]
    public void TheMembershipAnswerCountsAsADifferenceBetweenTwoRosters()
    {
        var member = Instance([Person("Jo", RosterStanding.Ordinary, true)]);
        var guest = Instance([Person("Jo", RosterStanding.Ordinary, false)]);

        Assert.False(member.LooksTheSameAs(guest));
    }
}
