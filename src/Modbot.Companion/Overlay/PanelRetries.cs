namespace Modbot.Companion.Overlay;

/// <summary>
/// A headset panel (or the SteamVR dashboard tab) that could not start: why, how often it has been
/// tried, and whether it is time to try again.
/// </summary>
/// <remarks>
/// <para><strong>Never the whole companion.</strong> A panel is the part that can wait. Reporting
/// presence is the part that cannot be filled in later, so whatever a panel throws on its way up —
/// a graphics card out of memory included, which is a <c>SharpGenException</c> or a
/// <c>COMException</c> rather than anything the old catches named — is caught at the start site,
/// logged, and shown as the panel's status, and the companion carries on. The one exception let
/// through is a cancel, which means the companion is shutting down.</para>
/// <para><strong>Tried again, slowly, and not for ever.</strong> The out-of-memory answer has come
/// and gone between starts on the same PC, so one failure is not taken as final. But a graphics
/// failure tried every ten seconds would be a graphics failure every ten seconds beside a game, so
/// a panel is tried at most once a minute and given up after <see cref="MostTries"/> tries in all,
/// until the moderator switches it off and on again or restarts.</para>
/// </remarks>
public sealed class PanelRetries
{
    /// <summary>The least time between two tries.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    /// <summary>Tries in all, the first start included, before the panel is given up on.</summary>
    public const int MostTries = 5;

    /// <summary>How many tries have failed since the panel last worked.</summary>
    public int Tries { get; private set; }

    /// <summary>The last failure in a few plain words, or null while nothing has failed.</summary>
    public string? Reason { get; private set; }

    /// <summary>When the last failed try was.</summary>
    public DateTimeOffset LastTriedAt { get; private set; }

    /// <summary>Whether the panel's last try failed.</summary>
    public bool Failing => Reason is not null;

    /// <summary>Whether the panel has failed too often to be tried again.</summary>
    public bool GaveUp => Failing && Tries >= MostTries;

    /// <summary>
    /// Counts one failed try. True when another will come, false when that was the last.
    /// </summary>
    public bool Failed(Exception failure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // A missing file or a PC that cannot do it at all will say the same in a minute, so those
        // use up every try at once.
        Tries = IsLasting(failure) ? MostTries : Tries + 1;
        Reason = ShortReason(failure);
        LastTriedAt = now;
        return !GaveUp;
    }

    /// <summary>Whether the panel is failing, not given up, and has waited long enough to be tried again.</summary>
    public bool Due(DateTimeOffset now) => Failing && !GaveUp && now - LastTriedAt >= Wait;

    /// <summary>The panel is up, or switched off: the count starts again from nothing.</summary>
    public void Clear()
    {
        Tries = 0;
        Reason = null;
        LastTriedAt = default;
    }

    /// <summary>The status line for a panel that could not start.</summary>
    public string Status => $"Could not start: {Reason}.";

    /// <summary>
    /// Whether a start site catches this. Everything but a cancel, which means the companion is
    /// shutting down and has to be let through.
    /// </summary>
    public static bool IsPanelFailure(Exception failure) => failure is not OperationCanceledException;

    /// <summary>
    /// Whether trying again could not change the answer: a file missing from the installation, or
    /// a PC that cannot draw a panel at all.
    /// </summary>
    public static bool IsLasting(Exception failure) => failure is DllNotFoundException or NotSupportedException;

    /// <summary><c>E_OUTOFMEMORY</c>.</summary>
    private const int OutOfMemory = unchecked((int)0x8007000E);

    /// <summary>The longest a reason taken from an error's own message is let run.</summary>
    private const int LongestReason = 120;

    /// <summary>Why a panel could not start, in a few plain words.</summary>
    public static string ShortReason(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.HResult == OutOfMemory || failure is OutOfMemoryException)
            return "the graphics card ran out of memory";

        if (failure is DllNotFoundException)
            return "a file it needs is missing from the Modbot installation";

        var message = failure.Message.Split('\n', 2)[0].Trim().TrimEnd('.');
        if (message.Length == 0)
            return failure.GetType().Name;

        return message.Length <= LongestReason ? message : message[..LongestReason].TrimEnd() + "…";
    }
}
