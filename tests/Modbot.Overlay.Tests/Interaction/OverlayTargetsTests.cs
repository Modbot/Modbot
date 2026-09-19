using Avalonia;
using Avalonia.Controls;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// What a tap can land on, found in the laid-out tree the way the runtime will find it, and the
/// pieces of the panel that only a controller brings: the cursor, a scrolled roster, a person's card.
/// </summary>
public class OverlayTargetsTests
{
    private const int Width = 480;
    private const int Height = 640;

    private static InstanceContext Roster(int people = 3) => new(
        "39911",
        [.. Enumerable.Range(0, people).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]);

    private static OverlayScreen Screen(
        FlaggedJoinAlert? alert = null,
        UserSummary? person = null,
        int skip = 0,
        PanelCursor? cursor = null,
        OverlayPage page = OverlayPage.Instance) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
        Freshness.Fresh,
        alert,
        Person: person,
        RosterSkip: skip,
        Cursor: cursor,
        Page: page);

    private static FlaggedJoinAlert Alert() => new("a1", "usr_1", "Person 1", "39911", "kicked before", 2, DateTimeOffset.UnixEpoch);

    private static Control LaidOut(OverlayScreen screen)
    {
        var root = OverlayView.Build(screen);
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        return root;
    }

    private static byte[] Render(OverlayScreen screen) => AvaloniaTestHost.Run(() =>
    {
        using var renderer = new AvaloniaFrameRenderer(Width, Height);
        return renderer.Render(OverlayView.Build(screen)).ToArray();
    });

    private static byte Alpha(byte[] pixels, int x, int y) => pixels[((y * Width) + x) * 4 + 3];

    [Fact]
    public void EveryRowTheAlertAndTheRosterAreTargets()
    {
        var found = AvaloniaTestHost.Run(() => OverlayTargets.Find(LaidOut(Screen(Alert()))));

        Assert.Contains(found, t => t.Target is OverlayTarget.DismissAlert);
        Assert.Contains(found, t => t.Target is OverlayTarget.Roster);
        Assert.Equal(3, found.Count(t => t.Target is OverlayTarget.Person));
        Assert.All(found, t => Assert.True(t.Bounds.Width > 0 && t.Bounds.Height > 0));
    }

    [Fact]
    public void ATapInARowIsThatPersonAndATapOnTheGroundIsNothing()
    {
        var (row, ground, onAlert) = AvaloniaTestHost.Run(() =>
        {
            var root = LaidOut(Screen(Alert()));
            var second = OverlayTargets.Find(root).First(t => t.Target is OverlayTarget.Person { SubjectId: "usr_1" });
            var alert = OverlayTargets.Find(root).First(t => t.Target is OverlayTarget.DismissAlert);

            return (
                OverlayTargets.At(root, second.Bounds.Center),
                OverlayTargets.At(root, new Point(Width / 2, Height - 4)),
                OverlayTargets.At(root, alert.Bounds.Center));
        });

        // The row beats the roster it sits in, because it is the deeper target.
        Assert.Equal(new OverlayTarget.Person("usr_1"), row);
        Assert.Null(ground);
        Assert.IsType<OverlayTarget.DismissAlert>(onAlert);
    }

    [Fact]
    public void TheCursorIsDrawnWhereTheHandPoints()
    {
        var without = Render(Screen());
        var with = Render(Screen(cursor: new PanelCursor(0.5f, 0.95f)));

        // Low on the panel, below the cards, the ground is clear until the cursor is there.
        Assert.Equal(0, Alpha(without, Width / 2, (int)(Height * 0.95)));
        Assert.NotEqual(0, Alpha(with, Width / 2, (int)(Height * 0.95)));
        Assert.False(Screen().LooksTheSameAs(Screen(cursor: new PanelCursor(0.5f, 0.95f))));
    }

    [Fact]
    public void AScrolledRosterShowsFewerRowsAndSaysHowManyAreAbove()
    {
        var rows = AvaloniaTestHost.Run(() => OverlayTargets.Find(LaidOut(Screen(skip: 2))).Count(t => t.Target is OverlayTarget.Person));

        Assert.Equal(1, rows);
        Assert.False(Screen().LooksTheSameAs(Screen(skip: 2)));
        Assert.NotEqual(Render(Screen()), Render(Screen(skip: 2)));
    }

    [Fact]
    public void APersonCardIsATargetThatCloses()
    {
        // The person is a screen of its own now, reached by tapping a row; the card carries Back
        // and Refresh rather than closing on a tap anywhere in it.
        var person = new UserSummary("usr_1", "Person 1", RosterStanding.Flagged, 2, DateTimeOffset.UnixEpoch, ["kicked before"], ["Regular"]);
        var open = Screen(person: person, page: OverlayPage.Person);

        var found = AvaloniaTestHost.Run(() => OverlayTargets.Find(LaidOut(open)));

        Assert.Contains(found, t => t.Target is OverlayTarget.ClosePerson);
        Assert.Contains(found, t => t.Target is OverlayTarget.RefreshPerson);
        Assert.False(Screen().LooksTheSameAs(open));
    }
}
