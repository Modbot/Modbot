namespace Modbot.Companion.Overlay;

/// <summary>
/// The people the Debug page's test events make up, told apart by their id.
/// </summary>
/// <remarks>
/// <para><strong>Nobody is ever asked about them.</strong> A made-up person exists only on this PC,
/// so every place that would otherwise ask a paired server about a person — opening their card,
/// placing a heads-up on them — checks <see cref="IsTest"/> first and sends nothing. The checks
/// sit both in the overlay's loop and in the two clients that make the requests, so a path added
/// later cannot get round the first without meeting the second.</para>
/// <para>The marker is not shaped like a VRChat id at all, so no real person can carry it.</para>
/// </remarks>
public static class TestPeople
{
    /// <summary>How every made-up person's id starts. One place, read by the test events and by every check.</summary>
    public const string Prefix = "modbot-test:";

    /// <summary>Whether this id is one the Debug page's test events made up.</summary>
    public static bool IsTest(string? subjectId)
        => subjectId is not null && subjectId.StartsWith(Prefix, StringComparison.Ordinal);
}
