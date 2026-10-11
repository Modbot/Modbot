using System.Numerics;
using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Interaction;

/// <summary>What a stick hold says after one look: how long is left, and whether it just finished.</summary>
/// <param name="SecondsLeft">Whole seconds still to go, counted up from one; null when no hold is under way.</param>
/// <param name="Progress">How far through the hold, 0 to 1, in steps of <see cref="StickHold.ProgressSteps"/>; zero when none is under way.</param>
/// <param name="Done">True on the one look the hold ran out.</param>
public readonly record struct StickHoldResult(int? SecondsLeft, float Progress, bool Done)
{
    /// <summary>No hold under way.</summary>
    public static StickHoldResult Idle { get; } = new(null, 0f, false);
}

/// <summary>
/// Counts how long a thumbstick has been held one way, for the show and hide button's shortcut.
/// </summary>
/// <remarks>
/// <para><strong>Held, then done once.</strong> The hold runs from the look the stick first points the
/// right way. When the time is up it is done, once; holding on does nothing more until the stick has
/// come back to the middle.</para>
/// <para><strong>A stick being used for something else never starts it.</strong> Pushing or pulling a
/// carried panel and scrolling a list both hold a stick back or forward for as long as they take. A
/// stick that is in use is ignored, and so is the rest of that push once the use ends: the hold waits
/// for the stick to come back to the middle first, so letting go of a carried panel with the stick
/// still pulled does not count as the start of a hold.</para>
/// <para><strong>Two thresholds.</strong> The stick has to be pushed well over to start
/// (<see cref="Engage"/>) but only has to stay part of the way over to carry on
/// (<see cref="Keep"/>), so one that wobbles around a single line does not keep starting again. A
/// stick off to the side of the way it is meant to be held does not count at all.</para>
/// <para>Time is whatever clock the caller polls with; only differences are used.</para>
/// </remarks>
public sealed class StickHold
{
    /// <summary>How far over the stick must be pushed along the chosen way to start a hold.</summary>
    public const float Engage = 0.75f;

    /// <summary>How far over it must stay to carry on. Lower than <see cref="Engage"/> on purpose.</summary>
    public const float Keep = 0.5f;

    /// <summary>How far off the chosen way, as a share of how far along it, still counts as that way.</summary>
    public const float Sideways = 0.5f;

    /// <summary>The bar is drawn in this many steps, so a hold does not redraw the button every look.</summary>
    public const int ProgressSteps = 20;

    private bool _engaged;
    private bool _blocked;
    private TimeSpan? _since;

    /// <summary>The direction a stick is pushed to, as the stick reports it: x to the right, y away from you.</summary>
    public static Vector2 Axis(StickDirection direction) => direction switch
    {
        StickDirection.Forward => new Vector2(0f, 1f),
        StickDirection.Left => new Vector2(-1f, 0f),
        StickDirection.Right => new Vector2(1f, 0f),
        _ => new Vector2(0f, -1f),
    };

    /// <summary>Forgets any hold under way and waits for the stick to come back to the middle.</summary>
    public void Reset()
    {
        _since = null;
        _blocked = _engaged;
    }

    /// <summary>One look at a stick.</summary>
    /// <param name="stick">The stick as the controller reports it, -1 to 1 on each axis.</param>
    /// <param name="direction">The way it has to be held.</param>
    /// <param name="hold">How long it has to be held.</param>
    /// <param name="free">False while the stick is in use for something else, which ignores it.</param>
    /// <param name="now">Any steadily rising clock.</param>
    public StickHoldResult Update(Vector2 stick, StickDirection direction, TimeSpan hold, bool free, TimeSpan now)
    {
        var axis = Axis(direction);
        var along = Vector2.Dot(stick, axis);
        var across = MathF.Abs((stick.X * axis.Y) - (stick.Y * axis.X));

        _engaged = along >= (_engaged ? Keep : Engage) && across <= along * Sideways;

        if (!_engaged)
        {
            _since = null;
            _blocked = false;
            return StickHoldResult.Idle;
        }

        if (!free)
        {
            _since = null;
            _blocked = true;
        }

        if (_blocked)
            return StickHoldResult.Idle;

        _since ??= now;
        var held = now - _since.Value;

        if (held >= hold)
        {
            _since = null;
            _blocked = true;
            return new StickHoldResult(null, 0f, true);
        }

        var left = hold - held;
        var seconds = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));
        var done = (float)(held.TotalSeconds / hold.TotalSeconds);
        var progress = MathF.Floor(done * ProgressSteps) / ProgressSteps;

        return new StickHoldResult(seconds, progress, false);
    }
}
