using Modbot.Companion.Overlay;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App;

/// <summary>The screens the debug page can pin into the overlay.</summary>
public enum OverlaySample
{
    Idle,
    Roster,
    FlaggedJoin,
    Problem,
}

/// <summary>
/// Made-up screens for looking at the overlay without a group instance, a server or a headset.
/// </summary>
/// <remarks>
/// Nothing here comes from a server or a log, and nothing here is sent anywhere: a sample is built
/// from the constants below and drawn by the same code that draws the live screen, which is the
/// point of it.
/// </remarks>
internal static class OverlaySamples
{
    private const string Group = "Sample group";
    private const string Instance = "wrld_sample:12345~group(grp_sample)~groupAccessType(members)";

    public static string Name(OverlaySample sample) => sample switch
    {
        OverlaySample.Idle => "idle",
        OverlaySample.Roster => "roster",
        OverlaySample.FlaggedJoin => "flagged join",
        OverlaySample.Problem => "problem",
        _ => sample.ToString(),
    };

    public static OverlayScreen Build(OverlaySample sample) => sample switch
    {
        OverlaySample.Roster => new OverlayScreen(
            Group,
            new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh),

        OverlaySample.FlaggedJoin => new OverlayScreen(
            Group,
            new Cached<InstanceContext>(Roster(), Freshness.Fresh, TimeSpan.Zero),
            Freshness.Fresh,
            Alert: new FlaggedJoinAlert(
                "sample-alert", "usr_sample_1", "Rin Sample", Instance, "kicked before", 2, DateTimeOffset.UnixEpoch)),

        OverlaySample.Problem => new OverlayScreen(
            Group,
            new Cached<InstanceContext>(Roster(), Freshness.Stale, TimeSpan.FromMinutes(3)),
            Freshness.Stale,
            Health: $"Cannot reach {Group}. Showing what was last known."),

        _ => OverlayScreen.Idle,
    };

    private static InstanceContext Roster() => new(
        Instance,
        [
            new RosterMember("usr_sample_1", "Rin Sample", RosterStanding.Flagged, 2, ["kicked before"]),
            new RosterMember("usr_sample_2", "Kai Sample", RosterStanding.Staff, 0, []),
            new RosterMember("usr_sample_3", "Mira Sample", RosterStanding.Member, 0, []),
            new RosterMember("usr_sample_4", "Jo Sample", RosterStanding.Ordinary, 0, []),
        ]);
}
