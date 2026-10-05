using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Interaction;

/// <summary>
/// A controller made up in code, for the test remote: it points at the main panel, pulls the
/// trigger, pushes the thumbstick and squeezes the grip, one poll at a time, without anybody
/// holding a controller.
/// </summary>
/// <remarks>
/// <para><strong>The real path, from the controller on.</strong> It is put in where the runtime's
/// controller reading comes in (<see cref="OverlayHost.MadeUp"/>): while it has something to do it
/// stands in for one hand, and everything after that — which hand is pointing, where the ray lands,
/// whether something drawn is under it, the trigger's press, the stick's rows, taking hold and
/// letting go — is the same code a real controller goes through.</para>
/// <para><strong>Its grip goes through <see cref="GripHold"/></strong>, the same rule that reads a
/// real grip's squeeze, so it takes hold above a quarter and lets go below fifteen hundredths.</para>
/// <para><strong>It is never there by accident.</strong> Only the test remote sets one, and the
/// remote only exists in a test copy started in debug mode. With nothing queued and nothing held
/// it stands in for nothing, and the real hand is read again.</para>
/// <para>UI thread only, like the host it is set on.</para>
/// </remarks>
public sealed class MadeUpHand
{
    /// <summary>How far in front of the panel the made-up controller is held, in metres.</summary>
    public const float Reach = 0.3f;

    private abstract record Step;

    /// <summary>Pointing at a point of the panel, with the trigger and the stick as given.</summary>
    private sealed record PointAt(float Across, float Down, bool Click, float StickY) : Step;

    /// <summary>Pointing at the middle and squeezing the grip: takes hold.</summary>
    private sealed record Take : Step;

    /// <summary>Holding, where the hand was left.</summary>
    private sealed record HoldStill : Step;

    /// <summary>Holding, with the panel carried to a place in front of the head.</summary>
    private sealed record HoldAt(Vector3 FromHead) : Step;

    /// <summary>The grip let go, where the hand is.</summary>
    private sealed record LetGo : Step;

    private readonly Queue<Step> _steps = new();
    private readonly GripHold _grip = new();
    private Step? _resting;
    private Hand _side = Hand.Right;
    private Pose _device = Pose.Identity;
    private Pose _heldOffset = Pose.Identity;

    /// <summary>Whether the grip is closed on the panel right now, from the poll that took hold to the one that let go.</summary>
    private bool _inHand;

    /// <summary>Polls still to come before what was asked has all been done.</summary>
    public int Pending => _steps.Count;

    /// <summary>Whether the made-up hand is holding the panel between commands.</summary>
    public bool Holding => _resting is HoldStill or HoldAt;

    /// <summary>Which hand it stands in for while it acts.</summary>
    public Hand Side => _side;

    /// <summary>A trigger press at a point of the panel: aim, press, release.</summary>
    /// <param name="across">0 at the panel's left edge, 1 at its right.</param>
    /// <param name="down">0 at the top, 1 at the bottom.</param>
    public void Tap(float across, float down)
    {
        _steps.Enqueue(new PointAt(across, down, false, 0f));
        _steps.Enqueue(new PointAt(across, down, true, 0f));
        _steps.Enqueue(new PointAt(across, down, false, 0f));
    }

    /// <summary>
    /// The thumbstick pushed fully while pointing at a point, long enough for this many rows;
    /// positive is down the list. One extra poll at rest after, so nothing is left part-way to a row.
    /// </summary>
    public void Scroll(float across, float down, int rows)
    {
        if (rows == 0)
            return;

        // Thumbstick down is a negative axis, and it scrolls towards the rows below.
        var stick = rows > 0 ? -1f : 1f;
        var polls = ((int)OverlayHost.ScrollPerRow * Math.Abs(rows)) + 1;

        _steps.Enqueue(new PointAt(across, down, false, 0f));
        for (var i = 0; i < polls; i++)
            _steps.Enqueue(new PointAt(across, down, false, stick));

        _steps.Enqueue(new PointAt(across, down, false, 0f));
    }

    /// <summary>Points at the middle of the panel and takes hold, then keeps holding where it is.</summary>
    public void Grab()
    {
        _steps.Enqueue(new PointAt(0.5f, 0.5f, false, 0f));
        _steps.Enqueue(new Take());
        _steps.Enqueue(new HoldStill());
        _resting = new HoldStill();
    }

    /// <summary>Carries the held panel to a place in front of the head, in metres: right, up, back.</summary>
    public void Move(Vector3 fromHead)
    {
        _steps.Enqueue(new HoldAt(fromHead));
        _resting = new HoldAt(fromHead);
    }

    /// <summary>Lets go of the panel where it is, and stands in for nothing afterwards.</summary>
    public void Release()
    {
        _steps.Enqueue(new LetGo());
        _resting = null;
    }

    /// <summary>Drops whatever was still to come and lets go, as if the controller had been put down.</summary>
    public void Forget()
    {
        _steps.Clear();
        _resting = null;
        _inHand = false;
        _grip.Forget();
    }

    /// <summary>
    /// The controllers as the panel should see them this poll: the runtime's own reading, with one
    /// hand replaced by the made-up one while it has something to do.
    /// </summary>
    /// <param name="real">What the runtime read.</param>
    /// <param name="placement">Where the panel is now, before this poll.</param>
    public OverlayTracking Apply(OverlayTracking real, OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var step = _steps.Count > 0 ? _steps.Dequeue() : _resting;
        if (step is null)
            return real;

        // A new run of pointing chooses its hand: never the one the panel is worn on, which the
        // panel leaves out of everything (OverlayInteraction.Ignoring).
        if (step is PointAt or Take && !_inHand)
            _side = placement.Anchor is OverlayAnchor.RightHand ? Hand.Left : Hand.Right;

        var hand = step switch
        {
            PointAt point => Pointing(real, placement, point.Across, point.Down, point.Click, point.StickY, squeeze: 0f),
            Take => Taking(real, placement),
            HoldStill => Held(_device, squeeze: 1f),
            HoldAt at => Held(Carried(real, at.FromHead), squeeze: 1f),
            _ => LetGoOf(),
        };

        return _side == Hand.Left ? real with { Left = hand } : real with { Right = hand };
    }

    private HandState Pointing(OverlayTracking real, OverlayPlacement placement, float across, float down, bool click, float stickY, float squeeze)
    {
        if (PanelGeometry.PanelPose(placement, real) is not { } panel)
            return HandState.Missing;

        var aim = Aim(panel, placement.Width, across, down);
        return new HandState(true, aim, aim, _grip.Squeeze(false, squeeze), click, new Vector2(0f, stickY));
    }

    private HandState Taking(OverlayTracking real, OverlayPlacement placement)
    {
        if (PanelGeometry.PanelPose(placement, real) is not { } panel)
            return HandState.Missing;

        _device = Aim(panel, placement.Width, 0.5f, 0.5f);

        // Where the panel sits from the hand, worked out the way the panel's own rules work it out
        // when they take hold, so carrying it later puts it where it was asked for.
        _heldOffset = panel.RelativeTo(_device);
        _inHand = true;
        return new HandState(true, _device, _device, _grip.Squeeze(false, 1f), false, Vector2.Zero);
    }

    private HandState LetGoOf()
    {
        _inHand = false;
        return Held(_device, squeeze: 0f);
    }

    private HandState Held(Pose device, float squeeze)
    {
        _device = device;
        return new HandState(true, device, device, _grip.Squeeze(false, squeeze), false, Vector2.Zero);
    }

    /// <summary>Where the hand has to be for the panel it holds to sit at a place in front of the head.</summary>
    private Pose Carried(OverlayTracking real, Vector3 fromHead)
    {
        var target = real.Head.Then(new Pose(fromHead, Quaternion.Identity));
        return target.Then(_heldOffset.Inverse());
    }

    /// <summary>A controller <see cref="Reach"/> in front of a point of the panel, pointing straight at it.</summary>
    private static Pose Aim(Pose panel, float width, float across, float down)
        => panel.Then(new Pose(new Vector3((across - 0.5f) * width, (0.5f - down) * width, Reach), Quaternion.Identity));
}
