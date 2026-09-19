using Modbot.Companion.Pipeline;

namespace Modbot.Companion.Clips;

/// <summary>Why the client is, or is not, keeping the last few minutes right now.</summary>
public enum ClipRecordingState
{
    /// <summary>Nobody turned it on. The default, and the state a fresh install is in.</summary>
    Off,

    /// <summary>On, but VRChat is not running, so there is nothing worth keeping.</summary>
    Waiting,

    /// <summary>On, VRChat is running, and the last few minutes are being kept.</summary>
    Recording,

    /// <summary>On, but this machine cannot do it — no Windows, or no encoder.</summary>
    NotOnThisMachine,

    /// <summary>On, but the folder cannot be written to. The settings screen says which folder and why.</summary>
    FolderUnusable,

    /// <summary>
    /// On, VRChat is running, but Windows has not handed over VRChat's window yet. A clip is that
    /// window, so until there is one there is nothing to keep.
    /// </summary>
    NoWindow,

    /// <summary>
    /// On, VRChat's window is there, and not one picture of it has been captured yet.
    /// </summary>
    /// <remarks>
    /// This is the state that stops a black file being written. The recorder can have a window, a
    /// graphics device and an encoder and still have copied nothing — VRChat has not been the
    /// window in front since recording started, or Windows has not handed over a picture of the
    /// screen it is on. Saving then would produce a file of the right length with nothing in it,
    /// and a moderator would find that out at the moment they needed the clip. So it is its own
    /// state, Save a clip cannot be pressed in it, and both screens say so.
    /// </remarks>
    NothingRecordedYet,

    /// <summary>On, VRChat is running, and setting the recorder up failed. Said once, in the window.</summary>
    Failed,
}

/// <summary>
/// The rule for when the client keeps the last few minutes: only with the switch on, and only while
/// VRChat is running.
/// </summary>
/// <remarks>
/// <para><strong>It reads nothing and sends nothing.</strong> It compares two values the client
/// already has — the settings and what the log reader believes is going on — and answers with one
/// word. There is no path from here to the disk, to the screen or to the network.</para>
/// <para><strong>"While VRChat is running" is the log, not the process list.</strong> The client
/// already knows whether VRChat is running, because lines are either arriving in VRChat's log or
/// they are not (<see cref="LogHealth"/>). Going looking for a running program instead would hand
/// this client the one capability <c>CompanionSourceGuardTests</c> bans outright, to learn something
/// it already knew.</para>
/// <para>A log the client has stopped understanding still counts as VRChat running: lines are
/// arriving, the moderator is in a world, and a moderator whose client needs updating should not
/// also quietly lose the recording they switched on.</para>
/// <para><strong>Finding VRChat's window is a separate question.</strong> Since 2026-09-19 a clip
/// is VRChat's own window rather than the whole monitor, so the recorder has to be handed that
/// window before there is anything to keep. Not having it yet is its own state rather than a
/// failure: the recorder stays up and asks again, because VRChat's window turns up a moment after
/// its log starts moving.</para>
/// </remarks>
public static class ClipRecordingRule
{
    /// <summary>What the client should be doing right now.</summary>
    /// <param name="settings">The Clips card as it stands.</param>
    /// <param name="log">What the log reader believes is going on.</param>
    /// <param name="folder">Whether the clips folder can be written to.</param>
    /// <param name="supported">Whether this machine can record at all.</param>
    /// <param name="failed">Whether setting the recorder up has already failed this run.</param>
    /// <param name="windowFound">
    /// Whether the recorder has VRChat's window. Null means there is no recorder to ask yet, which
    /// is the moment one is about to be built.
    /// </param>
    /// <param name="pictureTaken">
    /// Whether the recorder has captured at least one picture of that window. Null means there is
    /// no recorder to ask yet. False with a window found is
    /// <see cref="ClipRecordingState.NothingRecordedYet"/>: saving then would write a file with a
    /// black picture in it.
    /// </param>
    public static ClipRecordingState Decide(
        ClipSettings settings,
        LogHealthStatus log,
        ClipsFolderCheck folder,
        bool supported,
        bool failed = false,
        bool? windowFound = null,
        bool? pictureTaken = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.On)
            return ClipRecordingState.Off;

        if (!supported)
            return ClipRecordingState.NotOnThisMachine;

        if (!folder.IsUsable)
            return ClipRecordingState.FolderUnusable;

        if (failed)
            return ClipRecordingState.Failed;

        if (log is LogHealthStatus.Idle)
            return ClipRecordingState.Waiting;

        if (windowFound == false)
            return ClipRecordingState.NoWindow;

        return pictureTaken == false
            ? ClipRecordingState.NothingRecordedYet
            : ClipRecordingState.Recording;
    }

    /// <summary>
    /// Whether the recorder should be up in this state.
    /// </summary>
    /// <remarks>
    /// True for <see cref="ClipRecordingState.NoWindow"/> and
    /// <see cref="ClipRecordingState.NothingRecordedYet"/> as well as
    /// <see cref="ClipRecordingState.Recording"/>: the recorder is what asks Windows for VRChat's
    /// window and what captures the first picture of it, so taking it down for not having done
    /// either yet would tear it down and build it again every second for as long as it took.
    /// </remarks>
    public static bool ShouldRecord(ClipRecordingState state)
        => state is ClipRecordingState.Recording
            or ClipRecordingState.NoWindow
            or ClipRecordingState.NothingRecordedYet;
}

/// <summary>What the Clips card shows. Set by whatever owns the recorder.</summary>
/// <param name="Settings">The card's four values as settings hold them.</param>
/// <param name="State">Off, waiting, recording, or the reason it is none of those.</param>
/// <param name="Folder">The folder saved clips go into, and its problem when it has one.</param>
/// <param name="SavedClips">How many clips are in that folder.</param>
/// <param name="UsedBytes">How much room they take.</param>
/// <param name="LastSaved">The file name of the clip saved most recently this run, or null.</param>
/// <param name="LastProblem">What went wrong the last time something was tried, or null.</param>
/// <param name="Supported">
/// Whether this machine can record at all. Read separately from <paramref name="State"/>, which
/// answers Off before it looks at anything else: a moderator who turned it off chose that, and a
/// machine that cannot record did not, so the card has to tell them apart.
/// </param>
public sealed record ClipsStatus(
    ClipSettings Settings,
    ClipRecordingState State,
    ClipsFolderCheck Folder,
    int SavedClips,
    long UsedBytes,
    string? LastSaved = null,
    string? LastProblem = null,
    bool Supported = true)
{
    /// <summary>Before the host has said anything: off, with the default settings.</summary>
    public static ClipsStatus None { get; } = new(
        ClipSettings.Default,
        ClipRecordingState.Off,
        new ClipsFolderCheck(string.Empty, null),
        0,
        0);

    /// <summary>Whether Save a clip can do anything right now.</summary>
    public bool CanSave => State is ClipRecordingState.Recording;

    /// <summary>What the card says when this machine cannot record, or null when it can.</summary>
    public string? Unsupported => Supported
        ? null
        : "Keeping the last few minutes only works on Windows.";
}
