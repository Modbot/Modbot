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
/// </remarks>
public static class ClipRecordingRule
{
    /// <summary>What the client should be doing right now.</summary>
    /// <param name="settings">The Clips card as it stands.</param>
    /// <param name="log">What the log reader believes is going on.</param>
    /// <param name="folder">Whether the clips folder can be written to.</param>
    /// <param name="supported">Whether this machine can record at all.</param>
    /// <param name="failed">Whether setting the recorder up has already failed this run.</param>
    public static ClipRecordingState Decide(
        ClipSettings settings,
        LogHealthStatus log,
        ClipsFolderCheck folder,
        bool supported,
        bool failed = false)
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

        return log is LogHealthStatus.Idle
            ? ClipRecordingState.Waiting
            : ClipRecordingState.Recording;
    }

    /// <summary>Whether the picture should actually be going into a file in this state.</summary>
    public static bool ShouldRecord(ClipRecordingState state) => state is ClipRecordingState.Recording;
}

/// <summary>What the Clips card shows. Set by whatever owns the recorder.</summary>
/// <param name="Settings">The card's four values as settings hold them.</param>
/// <param name="State">Off, waiting, recording, or the reason it is none of those.</param>
/// <param name="Folder">The folder saved clips go into, and its problem when it has one.</param>
/// <param name="SavedClips">How many clips are in that folder.</param>
/// <param name="UsedBytes">How much room they take.</param>
/// <param name="LastSaved">The file name of the clip saved most recently this run, or null.</param>
/// <param name="LastProblem">What went wrong the last time something was tried, or null.</param>
public sealed record ClipsStatus(
    ClipSettings Settings,
    ClipRecordingState State,
    ClipsFolderCheck Folder,
    int SavedClips,
    long UsedBytes,
    string? LastSaved = null,
    string? LastProblem = null)
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
}
