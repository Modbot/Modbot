using Modbot.Companion.Overlay;
using Modbot.Core.Users;
using Modbot.Overlay.Views;

namespace Modbot.OverlayShot;

/// <summary>
/// Made-up screens, drawn the same way for both looks. Fake names and fake ids; nothing here comes
/// from a server, a log or a person.
/// </summary>
internal static class Samples
{
    public const string Group = "Cat Lounge";

    private const string Instance = "39911";

    private const string Me = "usr_sample_me";

    /// <summary>A fixed clock, so the join times in the pictures never change between runs.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 30, 0, TimeSpan.Zero);

    private static readonly RosterMember Moderator = Person(Me, "Moderator Sample", TrustRank.KnownUser, standing: RosterStanding.Staff);
    private static readonly RosterMember Aldric = Person("usr_sample_aldric", "Aldric Sample", TrustRank.TrustedUser, eighteen: true);
    private static readonly RosterMember Brindle = Person("usr_sample_brindle", "Brindle Sample", TrustRank.KnownUser);
    private static readonly RosterMember Cosima = Person("usr_sample_cosima", "Cosima Sample", TrustRank.User, eighteen: true);
    private static readonly RosterMember Dorian = Person("usr_sample_dorian", "Dorian Sample", TrustRank.TrustedUser, standing: RosterStanding.Member);
    private static readonly RosterMember Elowen = new(
        "usr_sample_elowen", "Elowen Sample", RosterStanding.Flagged, 2, ["2 kicks or bans", "5 warns"], TrustRank.User, true);
    private static readonly RosterMember Fennick = Person("usr_sample_fennick", "Fennick Sample", TrustRank.KnownUser, eighteen: true);
    private static readonly RosterMember Garrick = Person("usr_sample_garrick", "Garrick Sample", TrustRank.KnownUser);

    private static RosterMember Person(
        string id, string name, TrustRank rank, bool eighteen = false, RosterStanding standing = RosterStanding.Ordinary)
        => new(id, name, standing, 0, [], rank, eighteen ? true : null);

    private static LiveEvent Event(string id, string kind, RosterMember who, int minutesAgo)
        => new(
            id,
            id,
            kind,
            Now.AddMinutes(-minutesAgo),
            Instance,
            new LivePerson(who.SubjectId, who.DisplayName, RankWords(who.TrustRank), who.Standing, who.PriorActions, who.Flags, who.EighteenPlus),
            who == Elowen && kind == LiveEventKinds.PersonJoined,
            null,
            false);

    // What the server sends for a rank is the enum's own name, which the panel parses back.
    private static string? RankWords(TrustRank? rank) => rank?.ToString();

    /// <summary>The Instance list: seven people here, one just left.</summary>
    public static OverlayScreen Roster() => Screen(OverlayPage.Instance);

    /// <summary>The Audit Log: joins, leaves and the people who were here before the moderator.</summary>
    public static OverlayScreen AuditLog() => Screen(OverlayPage.Events);

    private static OverlayScreen Screen(OverlayPage page)
    {
        RosterMember[] here = [Moderator, Aldric, Brindle, Cosima, Dorian, Elowen, Fennick];

        // Newest first, as the panel keeps them.
        LiveEvent[] events =
        [
            Event("e8", LiveEventKinds.PersonJoined, Elowen, 1),
            Event("e7", LiveEventKinds.PersonLeft, Garrick, 2),
            Event("e6", LiveEventKinds.PersonJoined, Cosima, 4),
            Event("e5", LiveEventKinds.PersonJoined, Fennick, 6),
            Event("e4", LiveEventKinds.PersonJoined, Brindle, 12),
            Event("e3", LiveEventKinds.PersonJoined, Garrick, 20),
            Event("e2", LiveEventKinds.PersonHere, Aldric, 30),
            Event("e1", LiveEventKinds.PersonHere, Dorian, 30),
        ];

        // Null means "already here when the moderator arrived".
        var arrivals = new Dictionary<string, DateTimeOffset?>
        {
            [Me] = Now.AddMinutes(-30),
            [Aldric.SubjectId] = null,
            [Brindle.SubjectId] = Now.AddMinutes(-12),
            [Cosima.SubjectId] = Now.AddMinutes(-4),
            [Dorian.SubjectId] = null,
            [Elowen.SubjectId] = Now.AddSeconds(-45),
            [Fennick.SubjectId] = Now.AddMinutes(-6),
        };

        return new OverlayScreen(
            Group,
            new Cached<InstanceContext>(new InstanceContext(Instance, here), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Page: page,
            Events: events,
            Arrivals: arrivals,
            Now: Now,
            ModeratorArrived: Now.AddMinutes(-30),
            ModeratorId: Me,
            Left: [new RecentLeaver(Garrick, Now.AddSeconds(-20))],
            CanPlaceHeadsUps: true);
    }

    /// <summary>A flagged person walking in, as the notification overlay shows it.</summary>
    public static NotificationScreen Notification() => new(
    [
        new PopUp(
            "alert:sample",
            "Flagged user joined · " + Group,
            Elowen.DisplayName!,
            "2 kicks or bans · 5 warns",
            PopUpTone.Flagged,
            Elowen.TrustRank,
            true,
            Elowen.SubjectId),
    ]);
}
