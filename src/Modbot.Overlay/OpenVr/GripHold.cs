namespace Modbot.Overlay.OpenVr;

/// <summary>
/// Whether a hand is holding the panel, from how hard the grip is squeezed.
/// </summary>
/// <remarks>
/// <para><strong>Why not the grip button.</strong> SteamVR's <c>k_EButton_Grip</c> is a button it
/// works out for itself, and on a Valve Index it works it out from the force sensor with a
/// threshold set for "squeeze hard", because that is what a game wants for picking a thing up. A
/// moderator taking hold of a panel is not picking up a rock, and a panel that needs a hard
/// squeeze is a panel that does not get moved. So the squeeze is read as a number instead and
/// this decides.</para>
/// <para><strong>The two numbers.</strong> The squeeze that takes the panel is the moderator's own
/// (<see cref="Take"/>, the Grip sensitivity slider; three tenths of full travel unless it is
/// moved, the same as XSOverlay), and it is let go a tenth below that, but never below five
/// hundredths. The gap is what stops a hand sitting near the line from taking and dropping the
/// panel over and over; a tenth of full travel is wide enough to swallow a tremble and narrow
/// enough that letting go still feels immediate. The floor keeps a hand that is merely resting on
/// the grip from holding the panel for ever at the lightest setting.</para>
/// <para><strong>The button still counts.</strong> A Vive wand's grip is a switch with nothing
/// analogue behind it, and reads zero however hard it is held; on those controllers the button is
/// the only answer there is, so a pressed button is a hold whatever the number says. Nothing is
/// lost on the controllers that do have a number: their button only ever turns on above this
/// threshold anyway.</para>
/// <para>One of these per hand. It is a value with a memory, not a reading, because whether a
/// light squeeze counts depends on whether the panel was already held.</para>
/// </remarks>
public sealed class GripHold
{
    /// <summary>The squeeze that takes the panel unless the setting says otherwise.</summary>
    public const float DefaultTake = 0.3f;

    /// <summary>How far below <see cref="Take"/> the grip has to fall back before the panel is let go.</summary>
    public const float Gap = 0.1f;

    /// <summary>The lowest squeeze that still counts as a hold.</summary>
    public const float LowestLetGo = 0.05f;

    private float _take = DefaultTake;
    private bool _held;

    /// <summary>
    /// How far the grip has to be squeezed, of its full travel, before the panel is taken. Set from
    /// the Grip sensitivity slider; a value that is not a number is ignored.
    /// </summary>
    public float Take
    {
        get => _take;
        set
        {
            if (float.IsFinite(value))
                _take = value;
        }
    }

    /// <summary>How far it has to fall back before the panel is let go.</summary>
    public float LetGo => MathF.Max(LowestLetGo, _take - Gap);

    /// <summary>Whether the panel is being held right now.</summary>
    public bool Held => _held;

    /// <param name="button">What SteamVR says its own grip button is doing.</param>
    /// <param name="squeeze">How hard the grip is squeezed, 0 at rest and 1 at full travel. Zero on a controller with no analogue grip.</param>
    public bool Squeeze(bool button, float squeeze)
    {
        if (button)
            return _held = true;

        if (!float.IsFinite(squeeze))
            squeeze = 0f;

        _held = _held ? squeeze > LetGo : squeeze >= _take;
        return _held;
    }

    /// <summary>Forgets the hold, for a controller that has gone away.</summary>
    public void Forget() => _held = false;
}
