namespace Modbot.Overlay.Interaction;

/// <summary>
/// Where a list is scrolled to and how fast it is still going, in pixels of the list's own layout:
/// moved by the ray (a drag) or a stick, and kept moving after a drag is let go, slowing to a stop
/// like a phone's list.
/// </summary>
/// <remarks>
/// <para>No clock of its own and no layout: the caller says how long a step was and how far the list
/// can go (<c>max</c>, the content's height less the room it is shown in), so it is checked with
/// numbers alone. The list stops at both ends; there is nothing past them to pull into.</para>
/// <para>Offset grows towards the rows below, the way a list scrolled down shows later rows.</para>
/// </remarks>
public sealed class ListScroll
{
    /// <summary>How fast a fling slows down: its speed falls by this share of itself per second (as e to the minus this).</summary>
    public const double Friction = 2.5;

    /// <summary>A fling slower than this, in pixels a second, has stopped.</summary>
    public const double StopSpeed = 25;

    /// <summary>The fastest a fling may start, in pixels a second, so a flick of the wrist does not throw a list out of sight.</summary>
    public const double TopSpeed = 6000;

    /// <summary>How fast a stick pushed all the way scrolls, in pixels a second.</summary>
    public const double StickSpeed = 1100;

    /// <summary>How far back the speed of a drag is read from when it is let go.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    // Where the list was at each recent poll of a drag, oldest first. Only what is within the window.
    private readonly List<(TimeSpan At, double Offset)> _samples = [];

    /// <summary>How far the list is scrolled past its top, in pixels.</summary>
    public double Offset { get; private set; }

    /// <summary>How fast the list is going on its own after a drag was let go, in pixels a second; zero when it is still.</summary>
    public double Velocity { get; private set; }

    /// <summary>Whether a fling is under way, which is when the list needs to be moved and drawn without being touched.</summary>
    public bool Flinging => Velocity != 0;

    /// <summary>
    /// A touch on the list: a fling stops where it is, and what a drag before it did is forgotten.
    /// </summary>
    public void Touch()
    {
        Velocity = 0;
        _samples.Clear();
    }

    /// <summary>
    /// The list followed the ray by <paramref name="by"/> pixels (positive is towards the rows below).
    /// </summary>
    public void Drag(double by, double max, TimeSpan now)
    {
        Velocity = 0;
        Offset = Math.Clamp(Offset + by, 0, Math.Max(0, max));

        _samples.Add((now, Offset));
        _samples.RemoveAll(sample => now - sample.At > Window);
    }

    /// <summary>
    /// The trigger came up: the list goes on at the speed it was dragged at over the last moments, or
    /// stays where it is when the ray had stopped before then.
    /// </summary>
    public void Release(TimeSpan now)
    {
        _samples.RemoveAll(sample => now - sample.At > Window);

        Velocity = 0;

        if (_samples.Count >= 2)
        {
            var first = _samples[0];
            var last = _samples[^1];
            var seconds = (last.At - first.At).TotalSeconds;

            if (seconds > 0)
            {
                var speed = (last.Offset - first.Offset) / seconds;
                speed = Math.Clamp(speed, -TopSpeed, TopSpeed);
                Velocity = Math.Abs(speed) < StopSpeed ? 0 : speed;
            }
        }

        _samples.Clear();
    }

    /// <summary>
    /// A stick held for <paramref name="seconds"/>: the list moves at a speed that follows how far it
    /// is pushed (<paramref name="deflection"/> is -1 to 1, positive towards the rows below). Letting
    /// the stick go stops the list at once.
    /// </summary>
    public void Push(double deflection, double seconds, double max)
    {
        Velocity = 0;
        _samples.Clear();
        Offset = Math.Clamp(Offset + (deflection * StickSpeed * seconds), 0, Math.Max(0, max));
    }

    /// <summary>
    /// Lets a fling go on for <paramref name="seconds"/>, slowing as it goes, and stops it at an end.
    /// Returns whether the list moved.
    /// </summary>
    public bool Step(double seconds, double max)
    {
        if (Velocity == 0)
            return false;

        var top = Math.Max(0, max);
        var before = Offset;

        // A step that would take the speed under the stopping speed goes only as far as the moment it
        // gets there, so the fling stops at the same place however long the steps are.
        var stops = Math.Abs(Velocity) * Math.Exp(-Friction * seconds) < StopSpeed;
        var goes = stops ? Math.Log(Math.Abs(Velocity) / StopSpeed) / Friction : seconds;

        // The exact distance a speed that falls by a share of itself covers in this long, so a slow
        // poll and a fast one carry a fling the same way.
        Offset = Math.Clamp(Offset + (Velocity / Friction * (1 - Math.Exp(-Friction * goes))), 0, top);
        Velocity *= Math.Exp(-Friction * goes);

        if (stops || Offset <= 0 || Offset >= top)
            Velocity = 0;

        return Offset != before;
    }

    /// <summary>Holds the list inside its ends, for when its content has got shorter.</summary>
    public void Clamp(double max)
    {
        Offset = Math.Clamp(Offset, 0, Math.Max(0, max));
        if (Offset <= 0 || Offset >= max)
            Velocity = 0;
    }

    /// <summary>Back to the top and still: another list, or the same one with other things in it.</summary>
    public void Reset()
    {
        Offset = 0;
        Velocity = 0;
        _samples.Clear();
    }
}
