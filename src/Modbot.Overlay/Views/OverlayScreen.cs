using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Views;

/// <summary>A point on the panel as fractions: 0 at the left or top edge, 1 at the right or bottom.</summary>
public readonly record struct PanelCursor(float Across, float Down);

/// <summary>
/// Everything the overlay draws, in one immutable snapshot.
/// </summary>
/// <remarks>
/// <para><strong>A value, not a live view.</strong> The overlay renders from what the client
/// already holds — never from a request made while drawing — so the thing handed to the renderer
/// is a copy of the cache at a moment, with its age already worked out. That is what lets the
/// overlay show useful information when a server is unreachable instead of a spinner.</para>
/// <para><strong>It always says which group it is speaking for.</strong> A moderator staffing two
/// communities seeing a flag needs to know whose flag it is, and with several servers paired the
/// overlay follows the instance rather than picking one.</para>
/// </remarks>
/// <param name="GroupLabel">
/// The moderator's own label for the server whose instance this is. Null when they are not in a
/// group instance at all, which is most of anybody's VRChat use.
/// </param>
/// <param name="Freshness">How old the roster is, and whether to say so.</param>
/// <param name="Alert">A flagged user who just arrived, or null.</param>
/// <param name="Health">A Modbot fault worth interrupting for, or null.</param>
/// <param name="ShowIdleCard">Draw the idle card rather than nothing; only the debug page asks.</param>
/// <param name="Person">A person's card, opened by tapping their row, or null.</param>
/// <param name="RosterSkip">How many rows the roster has been scrolled past.</param>
/// <param name="Cursor">Where a controller points at the panel, or null when none does.</param>
public sealed record OverlayScreen(
    string? GroupLabel,
    Cached<InstanceContext> Roster,
    Freshness Freshness,
    FlaggedJoinAlert? Alert = null,
    string? Health = null,
    bool ShowIdleCard = false,
    UserSummary? Person = null,
    int RosterSkip = 0,
    PanelCursor? Cursor = null)
{
    /// <summary>
    /// Nothing to say: no group, no roster, no alert, no problem. Drawn as nothing at all unless
    /// <see cref="ShowIdleCard"/> asks for the card, which only the companion's debug page does.
    /// </summary>
    public bool IsIdle => GroupLabel is null && Roster.Value is null && Alert is null && Health is null;

    /// <summary>The overlay when the moderator is not in any managed group's instance.</summary>
    public static OverlayScreen Idle { get; } = new(
        null,
        new Cached<InstanceContext>(null, Freshness.Never, TimeSpan.Zero),
        Freshness.Never);

    /// <summary>The same screen with the cursor at a point, or with none; the idle screen stays idle.</summary>
    public OverlayScreen WithCursor(PanelCursor? cursor) => Cursor == cursor ? this : this with { Cursor = cursor };

    /// <summary>
    /// Whether two screens would draw the same pixels. The compositor uses this to avoid
    /// rasterising a frame nobody would see any difference in.
    /// </summary>
    public bool LooksTheSameAs(OverlayScreen other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return GroupLabel == other.GroupLabel
            && ShowIdleCard == other.ShowIdleCard
            && RosterSkip == other.RosterSkip
            && Cursor == other.Cursor
            && Person?.SubjectId == other.Person?.SubjectId
            && Freshness == other.Freshness
            && Health == other.Health
            && Alert?.AlertId == other.Alert?.AlertId
            && Roster.Describe() == other.Roster.Describe()
            && SameRoster(Roster.Value, other.Roster.Value);
    }

    private static bool SameRoster(InstanceContext? a, InstanceContext? b)
    {
        if (a is null || b is null)
            return ReferenceEquals(a, b);

        if (a.InstanceId != b.InstanceId || a.Members.Count != b.Members.Count)
            return false;

        for (var i = 0; i < a.Members.Count; i++)
        {
            var (left, right) = (a.Members[i], b.Members[i]);
            if (left.SubjectId != right.SubjectId
                || left.Standing != right.Standing
                || left.DisplayName != right.DisplayName
                || left.PriorActions != right.PriorActions
                || left.TrustRank != right.TrustRank
                || !left.Flags.SequenceEqual(right.Flags, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
