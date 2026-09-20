namespace Modbot.Companion.Sounds;

/// <summary>
/// Which of the client's five sounds is played.
/// </summary>
/// <remarks>
/// <para><strong>One family, not five noises.</strong> All five are made by the same arithmetic in
/// <see cref="Bleep"/> — struck notes, coming up softly, dying away with a tail — and differ only
/// in how many notes there are, how high they are, which way they go and how loud the whole thing
/// is made. A moderator who has heard two of them has heard the instrument, which is what makes
/// the third one recognisable as Modbot rather than as some other program on the PC.</para>
/// <para><strong>Five because a moderator asked for five.</strong> The 2026-09-18 design said one
/// sound and no vocabulary of tones. That was narrowed on 2026-09-19: the four that matter are
/// "something happened", "somebody flagged is here", "more than one", and "this needs you now",
/// and they are told apart by getting longer and higher rather than by being different instruments.
/// Nobody has to learn them; a moderator who never notices the difference still hears a
/// notification.</para>
/// </remarks>
public enum Tune
{
    /// <summary>One soft note. The ordinary traffic of an instance: somebody joined, left, was already there, changed avatar.</summary>
    Chime,

    /// <summary>Two notes a fifth apart. Somebody the server flagged has arrived.</summary>
    Alert,

    /// <summary>The two notes, then a gap, then the two notes again. More than one flagged arrival at once.</summary>
    AlertTwice,

    /// <summary>Three notes climbing, struck harder and brighter. Reporting has stopped, or the client can no longer see the instance.</summary>
    Urgent,

    /// <summary>Two quiet notes falling. Something that was wanting attention no longer does.</summary>
    AllClear,
}

/// <summary>
/// Which sound belongs to what, and which of two sounds is the more serious.
/// </summary>
/// <remarks>
/// <para><strong>The mapping is the whole of the decision.</strong> The moderator's table is
/// written in terms of what they care about; the client knows <see cref="NotificationKind"/>. This
/// is where one becomes the other, in one place, so the Notifications card, the rule and the sound
/// itself cannot disagree about which sound a thing gets.</para>
/// <para><strong>Nothing raises <see cref="Tune.AllClear"/>.</strong> The client has no event today
/// that means "this is over": a problem that stops reporting is never told that it has been put
/// right, a flagged arrival is never withdrawn, and a card that is dismissed is dismissed on the
/// overlay without anything coming back here. The sound exists and the Test button plays it, and
/// the day something does resolve it has a sound to make. Inventing a kind to justify it would
/// have been worse than saying so (design 2026-09-18 §2.6).</para>
/// </remarks>
public static class Tunes
{
    /// <summary>The five sounds, in the order the Notifications card lists their buttons.</summary>
    public static IReadOnlyList<Tune> All { get; } =
        [Tune.Chime, Tune.Alert, Tune.AlertTwice, Tune.Urgent, Tune.AllClear];

    /// <summary>Which sound one kind of event gets.</summary>
    /// <remarks>
    /// The four ordinary kinds share the chime because they are the same news — somebody moved —
    /// and a moderator who ticks all four wants to be told, not startled. A flagged arrival is the
    /// alert. A stopped log and a rejected device are the urgent one, because both of them mean
    /// the client is no longer doing the job it was left running to do, and neither stops on its
    /// own. <see cref="Tune.AlertTwice"/> is not here: it is the alert's own answer to more than
    /// one flagged arrival at once, and <see cref="BleepRule"/> decides it.
    /// </remarks>
    public static Tune For(NotificationKind kind) => kind switch
    {
        NotificationKind.FlaggedJoin => Tune.Alert,
        NotificationKind.Problem or NotificationKind.LogStopped => Tune.Urgent,
        NotificationKind.Test => Tune.Alert,
        _ => Tune.Chime,
    };

    /// <summary>
    /// How serious a sound is, for deciding whether one may cut into the quiet gap another left
    /// behind it. Bigger is more serious.
    /// </summary>
    /// <remarks>
    /// <see cref="Tune.AllClear"/> sits with the chime rather than above it. It says something has
    /// stopped needing attention, which is the one piece of news that is never worth interrupting
    /// anything for.
    /// </remarks>
    public static int Urgency(Tune tune) => tune switch
    {
        Tune.Alert => 1,
        Tune.AlertTwice => 2,
        Tune.Urgent => 3,
        _ => 0,
    };

    /// <summary>What the sound is called on the Notifications card and in the docs.</summary>
    public static string Name(Tune tune) => tune switch
    {
        Tune.Chime => "Chime",
        Tune.Alert => "Alert",
        Tune.AlertTwice => "Alert twice",
        Tune.Urgent => "Urgent",
        _ => "All clear",
    };
}
