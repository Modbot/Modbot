using Modbot.Companion.Pipeline;

namespace Modbot.Companion.Listening;

/// <summary>Why the client is, or is not, listening for a phrase right now.</summary>
public enum ListeningState
{
    /// <summary>Nobody turned it on. The default, and the state a fresh install is in.</summary>
    Off,

    /// <summary>On, but this machine cannot do it.</summary>
    NotOnThisMachine,

    /// <summary>On, and the phrase model is being fetched. Nothing is open yet.</summary>
    Getting,

    /// <summary>On, but the model could not be fetched, so there is nothing to listen with.</summary>
    NoModel,

    /// <summary>On, but VRChat is not running, so the microphone is closed.</summary>
    Waiting,

    /// <summary>On, VRChat is running, and the microphone is open.</summary>
    Listening,

    /// <summary>On, VRChat is running, but the microphone could not be opened.</summary>
    NoMicrophone,
}

/// <summary>
/// The rule for when the client listens: only with the switch on, only with the model on the disk,
/// and only while VRChat is running.
/// </summary>
/// <remarks>
/// <para><strong>It reads nothing and sends nothing.</strong> It compares values the client already
/// has and answers with one word. There is no path from here to a microphone, to the disk or to the
/// network.</para>
/// <para><strong>"While VRChat is running" is the log, not the process list.</strong> The same rule
/// the recorder uses (<see cref="Clips.ClipRecordingRule"/>) and for the same two reasons: the
/// client already knows, because lines are either arriving in VRChat's log or they are not
/// (<see cref="LogHealth"/>); and going looking for a running program would hand this client the
/// one capability <c>CompanionSourceGuardTests</c> bans outright, to learn something it already
/// knew.</para>
/// <para>That rule is what keeps this from being a microphone that is open whenever the client is.
/// A moderator who is not in VRChat has nothing open, however long the client has been in their
/// tray.</para>
/// <para>A log the client has stopped understanding still counts as VRChat running: lines are
/// arriving, the moderator is in a world, and somebody whose client needs updating should not also
/// quietly lose the thing they switched on.</para>
/// </remarks>
public static class ListeningRule
{
    /// <summary>What the client should be doing right now.</summary>
    /// <param name="settings">The card as it stands.</param>
    /// <param name="log">What the log reader believes is going on.</param>
    /// <param name="supported">Whether this machine can listen at all.</param>
    /// <param name="modelReady">Whether the phrase model is on the disk and checked.</param>
    /// <param name="getting">Whether the model is being fetched right now.</param>
    /// <param name="microphoneOpen">
    /// Whether the microphone is actually open. Null means there is nothing to ask yet, which is
    /// the moment one is about to be opened.
    /// </param>
    public static ListeningState Decide(
        ListeningSettings settings,
        LogHealthStatus log,
        bool supported,
        bool modelReady,
        bool getting = false,
        bool? microphoneOpen = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.On)
            return ListeningState.Off;

        if (!supported)
            return ListeningState.NotOnThisMachine;

        if (getting)
            return ListeningState.Getting;

        if (!modelReady)
            return ListeningState.NoModel;

        if (log is LogHealthStatus.Idle)
            return ListeningState.Waiting;

        return microphoneOpen == false ? ListeningState.NoMicrophone : ListeningState.Listening;
    }

    /// <summary>
    /// Whether the microphone should be open in this state.
    /// </summary>
    /// <remarks>
    /// True for <see cref="ListeningState.NoMicrophone"/> as well as
    /// <see cref="ListeningState.Listening"/>: the listener is what opens the microphone, so taking
    /// it down for not having got one would build it and tear it down once a second for as long as
    /// another program held the device.
    /// </remarks>
    public static bool ShouldListen(ListeningState state)
        => state is ListeningState.Listening or ListeningState.NoMicrophone;
}

/// <summary>What the Listening card shows. Set by whatever owns the listener.</summary>
/// <param name="Settings">The card's switch as settings hold it.</param>
/// <param name="State">Off, waiting, listening, or the reason it is none of those.</param>
/// <param name="Phrases">The phrases it listens for, as somebody would say them.</param>
/// <param name="Progress">How far the one download has got, 0 to 1.</param>
/// <param name="ModelBytes">How big that download is, so the card can say before it starts.</param>
/// <param name="LastHeard">The phrase heard most recently this run, or null.</param>
/// <param name="LastProblem">What went wrong the last time something was tried, or null.</param>
/// <param name="Supported">
/// Whether this machine can listen at all. Read separately from <paramref name="State"/>, which
/// answers Off before it looks at anything else: a moderator who turned it off chose that, and a
/// machine that cannot listen did not, so the card has to tell them apart.
/// </param>
/// <param name="Microphones">
/// The microphones this PC has, for the card's list. Empty while listening is switched off,
/// because a switched-off client asks Windows' audio system nothing at all.
/// </param>
/// <param name="MicrophoneMissing">
/// True when the moderator picked a microphone that is not plugged in, and the Windows default is
/// standing in for it.
/// </param>
public sealed record ListeningStatus(
    ListeningSettings Settings,
    ListeningState State,
    IReadOnlyList<string> Phrases,
    double Progress = 0,
    long ModelBytes = 0,
    string? LastHeard = null,
    string? LastProblem = null,
    bool Supported = true,
    IReadOnlyList<Microphone>? Microphones = null,
    bool MicrophoneMissing = false)
{
    /// <summary>The microphones this PC has, never null.</summary>
    public IReadOnlyList<Microphone> MicrophonesOrNone => Microphones ?? [];

    /// <summary>Before the host has said anything: off, with the default settings.</summary>
    public static ListeningStatus None { get; } = new(
        ListeningSettings.Default,
        ListeningState.Off,
        PhraseModel.Default.Spoken,
        ModelBytes: PhraseModel.Default.Size);

    /// <summary>True while the microphone is actually open.</summary>
    public bool IsListening => State is ListeningState.Listening;

    /// <summary>What the card says when this machine cannot listen, or null when it can.</summary>
    public string? Unsupported => Supported
        ? null
        : "Listening for a phrase only works on Windows.";
}
