using Modbot.Companion.Overlay;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;
using Modbot.Core.Users;

namespace Modbot.Overlay.Tests.Views;

/// <summary>
/// What the overlay shows when the network is behaving, and — more importantly — what it shows
/// when it is not. The moment the overlay matters most is a flagged user arriving, which is also
/// the moment a slow or dead server hurts, so "stale with its age stated" has to be a rendered
/// frame rather than a blank one.
/// </summary>
public class OverlayViewTests
{
    private const int Width = 480;
    private const int Height = 640;

    private static InstanceContext Roster(int flagged = 1) => new(
        "39911",
        [
            new RosterMember("usr_ord", "Ordinary Person", RosterStanding.Ordinary, 0, []),
            new RosterMember("usr_mem", "A Member", RosterStanding.Member, 0, []),
            .. Enumerable.Range(0, flagged).Select(i =>
                new RosterMember($"usr_flag{i}", $"Flagged {i}", RosterStanding.Flagged, 2, ["prior kick"])),
        ]);

    private static OverlayScreen Screen(
        Freshness freshness = Freshness.Fresh,
        TimeSpan age = default,
        FlaggedJoinAlert? alert = null,
        string? health = null)
        => new(
            "Cat Lounge",
            new Cached<InstanceContext>(Roster(), freshness, age),
            freshness,
            alert,
            health);

    private static byte[] Render(OverlayScreen screen) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Width, Height);
        return renderer.Render(OverlayView.Build(screen)).ToArray();
    });

    [Fact]
    public void RendersARosterWithoutThrowing()
    {
        var pixels = Render(Screen());

        Assert.Equal(Width * Height * 4, pixels.Length);
        Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 3);
    }

    [Fact]
    public void RendersWhenEveryServerIsUnreachableAndThereIsNothingCached()
    {
        // In a group instance, with every server unreachable and nothing cached: the only honest
        // blank, and it still draws a frame, because an overlay that goes dark when the network
        // does is backwards. (Outside a group instance the panel draws nothing on purpose.)
        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(null, Freshness.Never, TimeSpan.Zero),
            Freshness.Never);

        var pixels = Render(screen);

        Assert.Equal(Width * Height * 4, pixels.Length);
        Assert.True(pixels.Chunk(4).Select(p => (p[0], p[1], p[2])).Distinct().Count() > 1);
    }

    [Fact]
    public void StaleDataLooksDifferentFromFreshDataRatherThanIdentical()
    {
        // Freshness is shown, not implied. If these rendered the same, a moderator would have no
        // way to tell a twenty-minute-old roster from a current one.
        var fresh = Render(Screen(Freshness.Fresh));
        var stale = Render(Screen(Freshness.Stale, TimeSpan.FromMinutes(20)));

        Assert.False(fresh.AsSpan().SequenceEqual(stale));
    }

    [Fact]
    public void AnAlertChangesTheFrame()
    {
        var withAlert = Render(Screen(alert: new FlaggedJoinAlert(
            "alert-1", "usr_8f2c", "Rin", "39911", "two prior kicks", 2,
            new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero))));

        Assert.False(Render(Screen()).AsSpan().SequenceEqual(withAlert));
    }

    [Fact]
    public void AHealthFaultChangesTheFrame()
    {
        // Inside VRChat there is no email, no Discord and no browser. For the person moderating
        // at the moment it matters, this is the entire notification surface.
        var healthy = Render(Screen());
        var broken = Render(Screen(health: "Modbot rejected this device token. Reporting has stopped."));

        Assert.False(healthy.AsSpan().SequenceEqual(broken));
    }

    [Fact]
    public void SurvivesADisplayNameBuiltToBreakTheLayout()
    {
        // Display names are arbitrary user-controlled text: newlines, right-to-left overrides,
        // zero-width joiners and a name longer than the panel. None of it may push the rest of
        // the card out of the headset's view or take the renderer down.
        var hostile = new InstanceContext("39911",
        [
            new RosterMember("usr_a", "line\nbreak\nbreak\nbreak\nbreak", RosterStanding.Flagged, 1, ["x"]),
            new RosterMember("usr_b", new string('W', 4000), RosterStanding.Member, 0, []),
            new RosterMember("usr_c", "‮gnihtemos‭​​", RosterStanding.Staff, 0, []),
            new RosterMember("usr_d", null, RosterStanding.Ordinary, 0, []),
        ]);

        var screen = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(hostile, Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh);

        Assert.Equal(Width * Height * 4, Render(screen).Length);
    }

    [Fact]
    public void ATrustRankIsDrawnOnTheRowAndOnTheAlertCard()
    {
        // The rank is the one thing on a row the fact log does not carry, so a roster with ranks
        // must not render as the same pixels as one without.
        var ranked = new InstanceContext("39911",
        [
            new RosterMember("usr_ord", "Ordinary Person", RosterStanding.Ordinary, 0, [], TrustRank.KnownUser),
            new RosterMember("usr_mem", "A Member", RosterStanding.Member, 0, [], TrustRank.Nuisance),
            new RosterMember("usr_flag0", "Flagged 0", RosterStanding.Flagged, 2, ["prior kick"], TrustRank.VRChatTeam),
        ]);

        var withRanks = new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(ranked, Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh);

        Assert.False(Render(Screen()).AsSpan().SequenceEqual(Render(withRanks)));
        Assert.False(Screen().LooksTheSameAs(withRanks));

        var alert = new FlaggedJoinAlert("alert-1", "usr_8f2c", "Rin", "39911", "two prior kicks", 2, default);
        var plain = Render(Screen(alert: alert));
        var rankedAlert = Render(Screen(alert: alert with { TrustRank = TrustRank.NewUser }));

        Assert.False(plain.AsSpan().SequenceEqual(rankedAlert));
    }

    [Fact]
    public void TwoIdenticalScreensAreRecognisedAsIdenticalSoNoFrameIsDrawn()
    {
        Assert.True(Screen().LooksTheSameAs(Screen()));
    }

    [Theory]
    [MemberData(nameof(DifferingScreens))]
    public void AChangeWorthDrawingIsRecognisedAsOne(OverlayScreen other)
    {
        Assert.False(Screen().LooksTheSameAs(other));
    }

    public static TheoryData<OverlayScreen> DifferingScreens() =>
    [
        Screen(Freshness.Stale, TimeSpan.FromMinutes(20)),
        Screen(health: "ingest stopped"),
        Screen(alert: new FlaggedJoinAlert("a", "usr_x", "X", "39911", "why", 1, default)),
        new OverlayScreen(
            "Dog Lounge",
            new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh),
        new OverlayScreen(
            "Cat Lounge",
            new Cached<InstanceContext>(Roster(flagged: 2), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh),
    ];

    [Fact]
    public void TheIdleScreenIsDifferentFromAnyInstanceScreen()
    {
        Assert.False(OverlayScreen.Idle.LooksTheSameAs(Screen()));
        Assert.True(OverlayScreen.Idle.LooksTheSameAs(OverlayScreen.Idle));
    }
}
