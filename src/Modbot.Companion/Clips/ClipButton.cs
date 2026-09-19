namespace Modbot.Companion.Clips;

/// <summary>What the overlay's Save a clip control is showing.</summary>
public enum ClipButtonState
{
    /// <summary>Clips is off. Nothing is drawn at all.</summary>
    Hidden,

    /// <summary>The last few minutes are being kept. Pressing saves them.</summary>
    Ready,

    /// <summary>A clip has just been written. Said for a few seconds, then back to ready.</summary>
    Saved,

    /// <summary>The last press did not produce a file.</summary>
    NotSaved,

    /// <summary>On, but nothing is being kept. The caption says which of the reasons it is.</summary>
    Stopped,
}

/// <summary>The most recent attempt at saving a clip, and whether a file landed.</summary>
public readonly record struct ClipSave(DateTimeOffset At, bool Worked);

/// <summary>What the overlay draws for Save a clip: one caption, and whether it can be pressed.</summary>
public readonly record struct ClipButton(ClipButtonState State, string Caption, bool CanPress)
{
    /// <summary>Clips is off, so there is no control.</summary>
    public static ClipButton None { get; } = new(ClipButtonState.Hidden, string.Empty, false);

    /// <summary>Whether the overlay draws anything for this at all.</summary>
    public bool IsVisible => State is not ClipButtonState.Hidden;
}

/// <summary>
/// What the Save a clip control on the overlay panel says, and whether pressing it does anything.
/// </summary>
/// <remarks>
/// <para><strong>It never lies about what is being kept.</strong> A moderator wearing a headset
/// cannot see the settings screen, a file explorer or a notification, so a control that looked
/// pressable and quietly did nothing would leave them believing they had kept a moment they had
/// not. Every state where nothing is being recorded — the switch off, VRChat not running, a folder
/// that cannot be written to, a machine that cannot record, a window that has not been found —
/// gives a caption naming that and a control that cannot be pressed.</para>
/// <para><strong>And it confirms.</strong> After a clip lands the caption says so for a few
/// seconds, because inside a headset there is no other way to find out. If the save failed it says
/// that instead, for the same length of time.</para>
/// <para><strong>Off draws nothing.</strong> With Clips switched off there is no control on the
/// panel, because a control that cannot ever do anything is worse than no control — and a moderator
/// who never asked for this should not find it in front of them in VR.</para>
/// </remarks>
public static class ClipButtonRule
{
    /// <summary>How long the panel says a clip was, or was not, saved.</summary>
    public static readonly TimeSpan SaidFor = TimeSpan.FromSeconds(8);

    /// <summary>What the control shows right now.</summary>
    /// <param name="clips">The Clips card as the client last worked it out.</param>
    /// <param name="now">From <c>IModbotClock</c>; never the machine's own clock.</param>
    /// <param name="lastSave">The most recent save, or null when there has not been one this run.</param>
    public static ClipButton For(ClipsStatus clips, DateTimeOffset now, ClipSave? lastSave = null)
    {
        ArgumentNullException.ThrowIfNull(clips);

        if (!clips.Settings.On)
            return ClipButton.None;

        if (!clips.CanSave)
            return new ClipButton(ClipButtonState.Stopped, Why(clips.State), false);

        if (lastSave is { } save && now - save.At < SaidFor && now >= save.At)
        {
            return save.Worked
                ? new ClipButton(ClipButtonState.Saved, "Clip saved", true)
                : new ClipButton(ClipButtonState.NotSaved, "Clip not saved", true);
        }

        return new ClipButton(ClipButtonState.Ready, "Save a clip", true);
    }

    /// <summary>The same words the Clips card on the settings screen uses, so one thing has one name.</summary>
    private static string Why(ClipRecordingState state) => state switch
    {
        ClipRecordingState.Waiting => "Waiting for VRChat",
        ClipRecordingState.NoWindow => "Waiting for VRChat's window",
        ClipRecordingState.NothingRecordedYet => "Nothing recorded yet",
        ClipRecordingState.NotOnThisMachine => "Not available on this machine",
        ClipRecordingState.FolderUnusable => "The folder cannot be used",
        _ => "Recording stopped",
    };
}
