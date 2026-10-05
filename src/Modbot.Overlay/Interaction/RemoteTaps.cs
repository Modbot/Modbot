using Modbot.Companion.Overlay;

namespace Modbot.Overlay.Interaction;

/// <summary>
/// Which of the main panel's targets the test remote will not tap, because tapping them would ask
/// a server something about a real person, or send a heads-up.
/// </summary>
/// <remarks>
/// The remote sends nothing to any server. A test copy normally has nothing paired, but one that
/// has could otherwise be made to open a real person's card (a read from that server) or place a
/// heads-up (a post to it) without anybody in the headset. Test people are fine: nothing is ever
/// asked about them (<see cref="TestPeople"/>).
/// </remarks>
public static class RemoteTaps
{
    /// <summary>Why the remote will not tap this, or null when it may.</summary>
    /// <param name="target">What is under the point.</param>
    /// <param name="personShown">The person whose card the panel shows, if any.</param>
    public static string? Refusal(OverlayTarget? target, string? personShown) => target switch
    {
        OverlayTarget.Person row when !TestPeople.IsTest(row.SubjectId)
            => "That row is a real person; the remote only taps test people.",
        OverlayTarget.AddHeadsUp row when !TestPeople.IsTest(row.SubjectId)
            => "That row is a real person; the remote only taps test people.",
        OverlayTarget.RefreshPerson when !TestPeople.IsTest(personShown)
            => "Refresh asks a server about a real person; the remote never does.",
        OverlayTarget.PlaceHeadsUp or OverlayTarget.ClearHeadsUp
            => "Place and Clear send a heads-up to a server; the remote never sends anything.",
        _ => null,
    };
}
