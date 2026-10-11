using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;
using Modbot.Companion.TestRemote;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Views;

/// <summary>
/// What each panel is showing, written as JSON for the test remote's <c>state</c>.
/// </summary>
/// <remarks>
/// Read from what the panels already hold — the screen each last drew, where each is, how many
/// frames each has drawn — and nothing else. Names on the cards are what the panels show; a card
/// says <c>test</c> when its person is one the test events made up.
/// </remarks>
public static class TestRemoteState
{
    /// <summary>Where a panel is and how it is set.</summary>
    public static JsonObject Placement(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var offset = placement.Offset;
        return new JsonObject
        {
            ["anchor"] = Word(placement.Anchor.ToString()),
            ["offset"] = new JsonObject
            {
                ["x"] = Round(offset.X),
                ["y"] = Round(offset.Y),
                ["z"] = Round(offset.Z),
                ["qx"] = Round(offset.QX),
                ["qy"] = Round(offset.QY),
                ["qz"] = Round(offset.QZ),
                ["qw"] = Round(offset.QW),
            },
            ["width"] = Round(placement.Width),
            ["opacity"] = Round(placement.Opacity),
            ["curve"] = Round(placement.Curve),
            ["locked"] = placement.Locked,
            ["clickThrough"] = placement.ClickThrough,
        };
    }

    /// <summary>The main panel's screen: which page, whether idle, and the cards on it.</summary>
    public static JsonObject Screen(OverlayScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var cards = new JsonArray();
        if (screen.Alert is { } alert)
        {
            cards.Add(new JsonObject
            {
                ["kind"] = "flagged join",
                ["name"] = alert.DisplayName ?? alert.SubjectId,
                ["test"] = TestPeople.IsTest(alert.SubjectId),
                ["rank"] = alert.TrustRank?.ToString(),
                ["eighteenPlus"] = null,
                ["detail"] = alert.Reason,
                ["secondsLeft"] = null,
            });
        }

        if (screen.Person is { } person)
        {
            cards.Add(new JsonObject
            {
                ["kind"] = "person",
                ["name"] = person.DisplayName ?? person.SubjectId,
                ["test"] = TestPeople.IsTest(person.SubjectId),
                ["rank"] = person.TrustRank?.ToString(),
                ["eighteenPlus"] = null,
                ["detail"] = person.Flags.Count > 0 ? string.Join(", ", person.Flags) : null,
                ["secondsLeft"] = null,
            });
        }

        if (screen.Health is { } health)
        {
            cards.Add(new JsonObject
            {
                ["kind"] = "problem",
                ["name"] = null,
                ["test"] = false,
                ["rank"] = null,
                ["eighteenPlus"] = null,
                ["detail"] = health,
                ["secondsLeft"] = null,
            });
        }

        return new JsonObject
        {
            ["page"] = Word(screen.Page.ToString()),
            ["idle"] = screen.IsIdle,
            ["idleCard"] = screen.ShowIdleCard,
            ["notSynced"] = screen.NotSynced,
            ["group"] = screen.GroupLabel,
            ["roster"] = screen.HereCount,
            ["events"] = screen.EventsOrNone.Count,
            ["headsUps"] = screen.HeadsUpsOrNone.Count,
            ["rosterSkip"] = screen.RosterSkip,
            ["cursor"] = screen.Cursor is { } cursor ? new JsonObject { ["across"] = Round(cursor.Across), ["down"] = Round(cursor.Down) } : null,
            ["cards"] = cards,
        };
    }

    /// <summary>The main headset panel.</summary>
    /// <param name="host">The panel, or null when it is switched off or could not be set up.</param>
    /// <param name="on">The <strong>Overlay on</strong> switch.</param>
    public static JsonObject MainPanel(OverlayHost? host, bool on)
    {
        if (host is null)
            return new JsonObject { ["on"] = on, ["attached"] = false, ["built"] = false };

        return new JsonObject
        {
            ["on"] = on,
            ["built"] = true,
            ["attached"] = host.Status.State is OverlayRuntimeState.Running,
            ["status"] = host.Status.Detail,
            ["framesDrawn"] = host.FramesDrawn,
            ["size"] = new JsonObject { ["width"] = host.Width, ["height"] = host.Height },
            ["puttingBack"] = host.PuttingBack,
            ["holding"] = Hand(host.Holding),
            ["screen"] = Screen(host.Showing),
            ["placement"] = Placement(host.Placement),
        };
    }

    /// <summary>The cards a pop-up surface is showing, newest first, with the seconds each has left.</summary>
    public static JsonArray Cards(IReadOnlyList<PopUp> popUps, Func<string, TimeSpan?> timeLeft)
    {
        ArgumentNullException.ThrowIfNull(popUps);
        ArgumentNullException.ThrowIfNull(timeLeft);

        var cards = new JsonArray();
        foreach (var popUp in popUps)
        {
            cards.Add(new JsonObject
            {
                ["kind"] = Word(popUp.Tone.ToString()),
                ["heading"] = popUp.Heading,
                ["name"] = popUp.Body,
                ["test"] = TestPeople.IsTest(popUp.SubjectId),
                ["rank"] = popUp.Rank?.ToString(),
                ["eighteenPlus"] = popUp.EighteenPlus,
                ["detail"] = popUp.Detail,
                ["secondsLeft"] = timeLeft(popUp.Id) is { } left ? Math.Round(left.TotalSeconds, 1) : null,
            });
        }

        return cards;
    }

    /// <summary>The notification overlay in the headset.</summary>
    public static JsonObject NotificationPanel(NotificationHost? host, bool on, Func<string, TimeSpan?> timeLeft)
    {
        ArgumentNullException.ThrowIfNull(timeLeft);

        if (host is null)
            return new JsonObject { ["on"] = on, ["attached"] = false, ["built"] = false };

        return new JsonObject
        {
            ["on"] = on,
            ["built"] = true,
            ["attached"] = host.Status.State is OverlayRuntimeState.Running,
            ["status"] = host.Status.Detail,
            ["framesDrawn"] = host.FramesDrawn,
            ["holding"] = Hand(host.Holding),
            ["cards"] = Cards(host.Showing.PopUps, timeLeft),
            ["placement"] = Placement(host.Placement),
        };
    }

    /// <summary>The SteamVR dashboard tab, with every control as last drawn.</summary>
    public static JsonObject Dashboard(DashboardHost? host)
    {
        if (host is null)
            return new JsonObject { ["attached"] = false, ["built"] = false, ["controls"] = new JsonArray() };

        var controls = new JsonArray();
        foreach (var control in DashboardControls.Of(host))
        {
            controls.Add(new JsonObject
            {
                ["label"] = control.Label,
                ["kind"] = control.Kind,
                ["on"] = control.On,
                ["value"] = control.Value,
                ["x"] = Math.Round(control.Bounds.X, 1),
                ["y"] = Math.Round(control.Bounds.Y, 1),
                ["width"] = Math.Round(control.Bounds.Width, 1),
                ["height"] = Math.Round(control.Bounds.Height, 1),
            });
        }

        return new JsonObject
        {
            ["built"] = true,
            ["attached"] = host.Status.State is OverlayRuntimeState.Running,
            ["status"] = host.Status.Detail,
            ["framesDrawn"] = host.FramesDrawn,
            ["size"] = new JsonObject { ["width"] = host.Width, ["height"] = host.Height },
            ["controls"] = controls,
        };
    }

    private static string? Hand(Hand? hand) => hand switch
    {
        Interaction.Hand.Left => "left",
        Interaction.Hand.Right => "right",
        _ => null,
    };

    /// <summary>An enum's name as plain words: <c>LeftHand</c> is <c>left-hand</c>.</summary>
    private static string Word(string name) => RemoteCommands.Dashed(name);

    private static double Round(float value) => Math.Round(value, 3);
}
