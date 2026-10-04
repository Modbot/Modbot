using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Sounds;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Shared.HeadsUps;

namespace Modbot.Companion.Presentation;

/// <summary>What a test event on the Debug page pretends happened.</summary>
public enum TestEventKind
{
    Joined,
    Left,
    AlreadyThere,
    ChangedAvatar,
    FlaggedJoin,
    Problem,
    Pin,
    KeepAnEye,
    Message,
    AskForHelp,
}

/// <summary>One test event, as the Debug page's card reads.</summary>
/// <param name="Name">The made-up person's name. Blank means <see cref="TestEvents.DefaultName"/>.</param>
/// <param name="Rank">Their VRChat trust rank, or null for none.</param>
/// <param name="EighteenPlus">Whether they carry Modbot's 18+ mark.</param>
/// <param name="Reason">A flagged join's reason, a problem's words, or a heads-up's text. Blank means none.</param>
public sealed record TestEvent(
    TestEventKind Kind,
    string? Name = null,
    TrustRank? Rank = null,
    bool EighteenPlus = false,
    string? Reason = null);

/// <summary>
/// The Debug page's test events: made-up joins, leaves, flagged joins, problems and heads-ups, put
/// through the same paths on this PC that real ones take, so the pop-ups, the sound, the
/// notification overlay and the main panel can be tried without waiting for anybody to walk in.
/// </summary>
/// <remarks>
/// <para><strong>Only with <c>MODBOT_DEBUG_MODE=1</c>.</strong> Built only then, and
/// <see cref="Send"/> does nothing at all when it was built without it.</para>
/// <para><strong>It sends nothing to anybody.</strong> A person card goes to the
/// <see cref="EventNotifier"/>, which draws a card and plays a sound and nothing more; it is typed
/// as that class on purpose, so the event backup and the servers' queues, which take the same
/// observations through <see cref="CloudBackup.IObservationSink"/>, cannot be handed in instead.
/// A flagged join, and a join or a leave, also go to the overlay's own loop as if a paired server
/// had sent them (<c>OverlayDriver.TakeTestEvent</c>), which shows them and asks no server
/// anything. Nothing is written to the sent journal, the queues, settings.json or the log reader,
/// so nothing made up here can ever be reported to a server.</para>
/// <para><strong>The made-up people are marked.</strong> Each one's id starts with
/// <see cref="SubjectPrefix"/>, and their rank and 18+ mark are kept here, in memory, for the
/// cards to read the way they read what a server said.</para>
/// </remarks>
public sealed class TestEvents
{
    /// <summary>How every made-up person's id starts (<see cref="TestPeople.Prefix"/>).</summary>
    public const string SubjectPrefix = TestPeople.Prefix;

    /// <summary>Who a test event is about when the name is left blank.</summary>
    public const string DefaultName = "Test person";

    /// <summary>The avatar a test avatar change names.</summary>
    public const string TestAvatar = "Test avatar";

    /// <summary>Who a test heads-up says placed it.</summary>
    public const string TestPlacer = "Test";

    /// <summary>The time between the steps of <see cref="Run"/>.</summary>
    public static readonly TimeSpan RunGap = TimeSpan.FromSeconds(3);

    private readonly bool _on;
    private readonly IModbotClock _clock;
    private readonly EventNotifier _notices;
    private readonly PopUps? _popUps;
    private readonly Func<InstanceLocation?> _here;
    private readonly Func<LiveEvent, bool>? _live;
    private readonly Action<FlaggedJoinAlert>? _alertShown;
    private readonly Dictionary<string, PersonInfo> _people = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _sent;

    /// <param name="debugMode">Whether the client was started with <c>MODBOT_DEBUG_MODE=1</c>. False makes every send do nothing.</param>
    /// <param name="notices">The notifier real observations go to: the person cards and their sound.</param>
    /// <param name="popUps">The notification overlay's stack, for the cards that do not come from the notifier.</param>
    /// <param name="here">The instance the moderator is in, as the log last said, or null.</param>
    /// <param name="live">
    /// The overlay's loop, taking a made-up live event as if a paired server had sent it. False
    /// when no paired server covers the instance; then the card is put up here.
    /// </param>
    /// <param name="alertShown">The sound and voice for a flagged join, when the overlay's loop did not take it.</param>
    public TestEvents(
        bool debugMode,
        IModbotClock clock,
        EventNotifier notices,
        PopUps? popUps,
        Func<InstanceLocation?> here,
        Func<LiveEvent, bool>? live = null,
        Action<FlaggedJoinAlert>? alertShown = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(here);

        _on = debugMode;
        _clock = clock;
        _notices = notices;
        _popUps = popUps;
        _here = here;
        _live = live;
        _alertShown = alertShown;
    }

    /// <summary>Whether sending does anything: only in debug mode.</summary>
    public bool IsOn => _on;

    /// <summary>A made-up person's rank and 18+ mark, or null for anybody real.</summary>
    public PersonInfo? InfoOf(string subjectId)
    {
        lock (_gate)
            return _people.GetValueOrDefault(subjectId);
    }

    /// <summary>The id a made-up person with this name gets. The same name always gets the same id.</summary>
    public static string SubjectOf(string? name)
    {
        var letters = new string([.. Named(name).ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
        return SubjectPrefix + (letters.Length > 0 ? letters : "person");
    }

    /// <summary>The short run the Send a run button plays, <see cref="RunGap"/> apart: a join, a flagged join, an avatar change and a leave.</summary>
    public static IReadOnlyList<TestEvent> Run(TestEvent first)
    {
        ArgumentNullException.ThrowIfNull(first);

        return
        [
            first with { Kind = TestEventKind.Joined },
            first with { Kind = TestEventKind.FlaggedJoin },
            first with { Kind = TestEventKind.ChangedAvatar },
            first with { Kind = TestEventKind.Left },
        ];
    }

    /// <summary>
    /// Puts one test event through the paths a real one of its kind takes.
    /// </summary>
    /// <returns>False, and nothing done, outside debug mode.</returns>
    public bool Send(TestEvent test)
    {
        ArgumentNullException.ThrowIfNull(test);

        if (!_on)
            return false;

        var name = Named(test.Name);
        var subject = SubjectOf(name);
        var info = new PersonInfo(test.Rank, test.EighteenPlus);
        var reason = string.IsNullOrWhiteSpace(test.Reason) ? null : test.Reason.Trim();
        var number = Interlocked.Increment(ref _sent);

        lock (_gate)
            _people[subject] = info;

        switch (test.Kind)
        {
            case TestEventKind.Joined:
                Person(PresenceKind.Joined, LiveEventKinds.PersonJoined, subject, name, test, number);
                break;

            case TestEventKind.Left:
                Person(PresenceKind.Left, LiveEventKinds.PersonLeft, subject, name, test, number);
                break;

            case TestEventKind.AlreadyThere:
                Person(PresenceKind.PresenceObserved, LiveEventKinds.PersonHere, subject, name, test, number);
                break;

            case TestEventKind.ChangedAvatar:
                Person(PresenceKind.AvatarChanged, null, subject, name, test, number);
                break;

            case TestEventKind.FlaggedJoin:
                Flagged(subject, name, test, reason, info, number);
                break;

            case TestEventKind.Problem:
                _popUps?.Show(
                    new PopUp("problem-test", "Modbot", reason ?? "Test problem", null, PopUpTone.Problem),
                    NotificationKind.Problem);
                break;

            case TestEventKind.Pin or TestEventKind.AskForHelp:
                _popUps?.Show(PopUp.HeadsUp($"test-{number}", HeadsUpKindOf(test.Kind), TestPlacer, null, reason, null));
                break;

            case TestEventKind.KeepAnEye or TestEventKind.Message:
                _popUps?.Show(PopUp.HeadsUp($"test-{number}", HeadsUpKindOf(test.Kind), TestPlacer, name, reason, info));
                break;
        }

        return true;
    }

    /// <summary>
    /// A person arriving, leaving, already there or changing avatar: the notifier's card and sound,
    /// and for the first three a line on the main panel's Events screen.
    /// </summary>
    private void Person(PresenceKind kind, string? liveKind, string subject, string name, TestEvent test, int number)
    {
        var here = Here();

        _notices.Offer(
        [
            new ObservedPresence(
                kind,
                _clock.UtcNow.ToLocalTime().DateTime,
                subject,
                name,
                here,
                kind is PresenceKind.AvatarChanged ? TestAvatar : null),
        ]);

        if (liveKind is not null)
            _live?.Invoke(Live(liveKind, subject, name, test, RosterStanding.Ordinary, flagged: false, reason: null, here, number));
    }

    /// <summary>
    /// A flagged join: through the overlay's loop when a paired server covers the instance, which
    /// is what puts it on the main panel; otherwise the same card and sound put up here.
    /// </summary>
    private void Flagged(string subject, string name, TestEvent test, string? reason, PersonInfo info, int number)
    {
        var here = Here();
        var live = Live(LiveEventKinds.FlaggedJoin, subject, name, test, RosterStanding.Flagged, flagged: true, reason, here, number);

        if (_live?.Invoke(live) == true || live.ToAlert() is not { } alert)
            return;

        alert = alert with { TrustRank = test.Rank };
        _popUps?.Show(PopUp.FlaggedJoin(alert, null, info), NotificationKind.FlaggedJoin);
        _alertShown?.Invoke(alert);
    }

    private LiveEvent Live(
        string kind, string subject, string name, TestEvent test, RosterStanding standing, bool flagged, string? reason,
        InstanceLocation here, int number)
    {
        var id = $"test-{number}";

        return new LiveEvent(
            id,
            id,
            kind,
            _clock.UtcNow,
            here.InstanceId,
            new LivePerson(subject, name, test.Rank?.ToString(), standing, 0, [], test.EighteenPlus),
            flagged,
            reason,
            ByThisDevice: false,
            here.WorldId);
    }

    /// <summary>The moderator's instance, or a made-up one when the log has not named one.</summary>
    private InstanceLocation Here()
    {
        if (_here() is { } here)
            return here;

        InstanceLocation.TryParse("wrld_test:00000", out var made);
        return made;
    }

    private static string Named(string? name)
        => string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();

    private static HeadsUpKind HeadsUpKindOf(TestEventKind kind) => kind switch
    {
        TestEventKind.Pin => HeadsUpKind.Pin,
        TestEventKind.KeepAnEye => HeadsUpKind.KeepAnEye,
        TestEventKind.Message => HeadsUpKind.Message,
        _ => HeadsUpKind.AskForHelp,
    };
}
