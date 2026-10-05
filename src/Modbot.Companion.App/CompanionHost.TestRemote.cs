using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Modbot.Companion.Presentation;
using Modbot.Companion.TestRemote;
using Modbot.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Views;
using Serilog;

namespace Modbot.Companion.App;

/// <summary>
/// The test remote: lets a program on this PC press the dashboard tab's controls, point at the
/// main panel with a made-up controller, send test events and read what every panel is showing,
/// while somebody tries the companion out in a headset with nobody holding the controllers.
/// </summary>
/// <remarks>
/// <para><strong>Only in a test copy started in debug mode</strong> (<see cref="TestRemoteSwitch"/>).
/// Any other copy builds no listener and runs none of this.</para>
/// <para><strong>The same paths as a person.</strong> A press goes through the tab's pointer
/// handling, a tap through the made-up controller the panel reads in place of a real one, Put it
/// back through the button's own handler and a test event through the Debug page's Send. Nothing
/// here sets a setting directly.</para>
/// <para><strong>It sends nothing to any server</strong> and never starts SteamVR. A tap that would
/// ask a server about a real person, or send a heads-up, is refused (<see cref="RemoteTaps"/>); test
/// events only ever make up people. Settings it changes are the test copy's own, in its own folder.</para>
/// </remarks>
internal sealed partial class CompanionHost
{
    /// <summary>Stops the test remote's listener when this copy quits.</summary>
    private readonly CancellationTokenSource _testRemoteStop = new();

    /// <summary>The made-up controller the remote points with; null until the remote first needs it.</summary>
    private MadeUpHand? _madeUp;

    private void StartTestRemote()
    {
        if (_state is null || TestRemoteSwitch.PipeName(_data, _state.DebugMode) is not { } pipe)
            return;

        var server = new TestRemoteServer(pipe);
        _ = server.ListenAsync(
            line => RemoteAnswers.AnswerAsync(
                line,
                command => Dispatcher.UIThread.InvokeAsync(() => CarryOutAsync(command)),
                _testRemoteStop.Token),
            _testRemoteStop.Token);

        Log.Information("Test remote: listening on a pipe only this Windows account can open ({Pipe})", pipe);
    }

    /// <summary>One command, on the UI thread, where every panel lives.</summary>
    private async Task<JsonObject> CarryOutAsync(RemoteCommand command)
    {
        Log.Debug("Test remote: {Command}", command);

        switch (command)
        {
            case RemoteCommand.State:
                return RemoteState();

            case RemoteCommand.Press press:
            {
                var tab = DrawnDashboard();
                var pressed = DashboardControls.Press(tab, press.Label)
                    ?? throw new RemoteRefusal($"No control called \"{press.Label}\". Controls: {ControlLabels(tab)}");
                return new JsonObject { ["pressed"] = pressed.Label, ["kind"] = pressed.Kind, ["now"] = ControlNow(tab, pressed.Label) };
            }

            case RemoteCommand.Slide slide:
            {
                var tab = DrawnDashboard();
                var slid = DashboardControls.Slide(tab, slide.Label, slide.Fraction)
                    ?? throw new RemoteRefusal($"No slider called \"{slide.Label}\". Sliders: {string.Join(", ", Enum.GetValues<DashboardSlider>().Select(DashboardControls.SliderName))}");
                return new JsonObject { ["slid"] = slid.Label, ["now"] = ControlNow(tab, slid.Label) };
            }

            case RemoteCommand.TapPanel tap:
                return await TapPanelAsync(tap.X, tap.Y);

            case RemoteCommand.ScrollPanel scroll:
                return await ScrollPanelAsync(scroll);

            case RemoteCommand.PutBack:
                PutOverlayBack();
                return new JsonObject { ["placement"] = _overlayHost is { } panel ? TestRemoteState.Placement(panel.Placement) : null };

            case RemoteCommand.Event sent:
                return SendFromRemote(sent.Test);

            case RemoteCommand.Run run:
            {
                if (_testEvents is not { IsOn: true })
                    throw new RemoteRefusal("Test events are not running in this copy.");

                if (RemoteCommands.RefuseRealPerson(run.First.Name) is { } refused)
                    throw new RemoteRefusal(refused);

                SendTestRun(run.First);
                return new JsonObject
                {
                    ["started"] = TestEvents.Run(run.First).Count,
                    ["secondsApart"] = TestEvents.RunGap.TotalSeconds,
                    ["subject"] = TestEvents.SubjectOf(run.First.Name),
                };
            }

            case RemoteCommand.ClearCards:
                _popUps?.ClearAll();
                ShowPopUps();
                return new JsonObject { ["cleared"] = true };

            case RemoteCommand.GrabPanel:
            {
                var (host, hand) = Pointing();
                if (hand.Holding)
                    throw new RemoteRefusal("The made-up hand is already holding the panel.");

                hand.Grab();
                await DoneAsync(hand);
                if (host.Holding is null)
                {
                    hand.Release();
                    await DoneAsync(hand);
                    throw new RemoteRefusal("The panel was not taken: it is locked, lets rays through, or nothing is drawn in its middle.");
                }

                return PanelNow(host);
            }

            case RemoteCommand.MovePanel move:
            {
                var (host, hand) = Pointing();
                if (!hand.Holding)
                    throw new RemoteRefusal("Nothing is holding the panel: grab-panel first.");

                hand.Move(new Vector3(move.X, move.Y, move.Z));
                await DoneAsync(hand);
                return PanelNow(host);
            }

            case RemoteCommand.ReleasePanel:
            {
                var (host, hand) = Pointing();
                if (!hand.Holding)
                    throw new RemoteRefusal("Nothing is holding the panel.");

                hand.Release();
                await DoneAsync(hand);
                return PanelNow(host);
            }

            default:
                throw new RemoteRefusal("That command is not carried out here.");
        }
    }

    private JsonObject RemoteState()
    {
        var settings = _state!.Settings;
        var notifyDwell = settings.NotifyOverlay.Dwell;
        var desktop = settings.DesktopOverlay;
        var window = _desktopOverlay;

        return new JsonObject
        {
            ["folder"] = _data.Path,
            ["panel"] = TestRemoteState.MainPanel(_overlayHost, settings.OverlayOn),
            ["notifications"] = TestRemoteState.NotificationPanel(
                _notifyHost, settings.NotifyOverlay.On, id => _popUps?.TimeLeft(id, notifyDwell)),
            ["dashboard"] = TestRemoteState.Dashboard(_dashboard),
            ["desktop"] = new JsonObject
            {
                ["on"] = desktop.On,
                ["built"] = window is not null,
                ["shown"] = window?.IsVisible is true,
                ["framesDrawn"] = window?.FramesDrawn ?? 0,
                ["screen"] = window?.Showing is { } screen ? TestRemoteState.Screen(screen) : null,
                ["placement"] = new JsonObject
                {
                    ["opacity"] = desktop.Opacity,
                    ["locked"] = desktop.Locked,
                    ["clickThrough"] = desktop.ClickThrough,
                },
            },
        };
    }

    /// <summary>The dashboard tab, when it is drawn; otherwise why there is nothing to press.</summary>
    private DashboardHost DrawnDashboard()
    {
        if (_dashboard is null)
            throw new RemoteRefusal("There is no SteamVR dashboard tab on this PC.");

        if (_dashboard.Targets.Count == 0)
            throw new RemoteRefusal("The dashboard tab is not drawn: SteamVR is not running. The remote never starts it.");

        return _dashboard;
    }

    private static string ControlLabels(DashboardHost tab)
        => string.Join(", ", DashboardControls.Of(tab).Select(c => c.Label));

    /// <summary>A control as drawn after the press, which the companion has saved and drawn again by now.</summary>
    private static JsonObject? ControlNow(DashboardHost tab, string label)
        => DashboardControls.Find(DashboardControls.Of(tab), label) is { } now
            ? new JsonObject { ["on"] = now.On, ["value"] = now.Value }
            : null;

    private JsonObject SendFromRemote(TestEvent test)
    {
        if (_testEvents is not { IsOn: true })
            throw new RemoteRefusal("Test events are not running in this copy.");

        // Made-up people only. The parser refused a VRChat id already; this is the second check,
        // at the place the event is sent from.
        if (RemoteCommands.RefuseRealPerson(test.Name) is { } refused)
            throw new RemoteRefusal(refused);

        SendTestEvent(test);
        return new JsonObject
        {
            ["sent"] = RemoteCommands.Dashed(test.Kind.ToString()),
            ["subject"] = TestEvents.SubjectOf(test.Name),
        };
    }

    /// <summary>The main panel, attached, and the made-up controller pointed at it.</summary>
    private (OverlayHost Host, MadeUpHand Hand) Pointing()
    {
        if (_overlayHost is not { } host)
            throw new RemoteRefusal("The main panel is off.");

        if (host.Status.State is not OverlayRuntimeState.Running)
            throw new RemoteRefusal("The main panel is not attached: SteamVR is not running. The remote never starts it.");

        _madeUp ??= new MadeUpHand();
        host.MadeUp = _madeUp;
        return (host, _madeUp);
    }

    private async Task<JsonObject> TapPanelAsync(double x, double y)
    {
        var (host, hand) = Pointing();
        if (hand.Holding)
            throw new RemoteRefusal("The made-up hand is holding the panel: release-panel first.");

        if (x >= host.Width || y >= host.Height)
            throw new RemoteRefusal($"The panel's picture is {host.Width} by {host.Height}.");

        var (across, down) = ((float)(x / host.Width), (float)(y / host.Height));
        var under = host.TargetAt(across, down);
        if (RemoteTaps.Refusal(under, host.Showing.Person?.SubjectId) is { } refused)
            throw new RemoteRefusal(refused);

        var tapped = new JsonArray();
        void Heard(OverlayTarget? target) => tapped.Add(Name(target));

        host.Tapped += Heard;
        try
        {
            hand.Tap(across, down);
            await DoneAsync(hand);
        }
        finally
        {
            host.Tapped -= Heard;
        }

        return new JsonObject
        {
            ["under"] = Name(under),
            ["tapped"] = tapped,
            ["screen"] = TestRemoteState.Screen(host.Showing),
        };
    }

    private async Task<JsonObject> ScrollPanelAsync(RemoteCommand.ScrollPanel scroll)
    {
        var (host, hand) = Pointing();
        if (hand.Holding)
            throw new RemoteRefusal("The made-up hand is holding the panel: release-panel first.");

        var x = scroll.X < 0 ? host.Width / 2.0 : scroll.X;
        var y = scroll.Y < 0 ? host.Height / 2.0 : scroll.Y;
        if (x >= host.Width || y >= host.Height)
            throw new RemoteRefusal($"The panel's picture is {host.Width} by {host.Height}.");

        var rows = 0;
        void Heard(int by) => rows += by;

        host.RosterScrolled += Heard;
        try
        {
            hand.Scroll((float)(x / host.Width), (float)(y / host.Height), scroll.Rows);
            await DoneAsync(hand);
        }
        finally
        {
            host.RosterScrolled -= Heard;
        }

        return new JsonObject
        {
            ["under"] = Name(host.TargetAt((float)(x / host.Width), (float)(y / host.Height))),
            ["scrolled"] = rows,
        };
    }

    private static JsonObject PanelNow(OverlayHost host) => new()
    {
        ["holding"] = host.Holding?.ToString().ToLowerInvariant(),
        ["placement"] = TestRemoteState.Placement(host.Placement),
    };

    private static string? Name(OverlayTarget? target) => target?.ToString();

    /// <summary>
    /// Waits for the controller loop to have read every step the made-up hand was given, then one
    /// more turn, so whatever the last one set off has happened.
    /// </summary>
    private static async Task DoneAsync(MadeUpHand hand)
    {
        // Timed with the stopwatch's counter: only how long it has been matters.
        var started = Stopwatch.GetTimestamp();
        var limit = TimeSpan.FromMilliseconds(1000 + (hand.Pending * 100));

        while (hand.Pending > 0)
        {
            if (Stopwatch.GetElapsedTime(started) > limit)
            {
                hand.Forget();
                throw new RemoteRefusal("The controllers were not read in time; is the main panel attached?");
            }

            await Task.Delay(15);
        }

        await Task.Delay(50);
    }
}
