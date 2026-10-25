namespace Modbot.Shared.Clips;

/// <summary>
/// What a companion tells its server about its Clips settings, kept where both read it
/// (automatic roles, 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// Sent over the live connection the companion already holds: once when it opens, and again
/// whenever one of the four values changes, as <c>{"op":"clips","on":...}</c> on the WebSocket and
/// as the <c>clipsOn</c>, <c>clipsRecording</c>, <c>clipsMinutes</c> and <c>clipsSendForActions</c>
/// query values on a long poll. Nothing else about the clips is told: not a file, not a name, not a
/// folder.
/// </para>
/// <para>
/// The server keeps the latest report per device in memory and asks one question of it,
/// <see cref="IsReady"/>.
/// </para>
/// </remarks>
/// <param name="On">Clips is switched on.</param>
/// <param name="Recording">The recorder is running right now and has a picture.</param>
/// <param name="Minutes">The length setting, in minutes.</param>
/// <param name="SendForActions">"Send clips for my actions automatically" is ticked.</param>
public sealed record ClipSettingsReport(bool On, bool Recording, int Minutes, bool SendForActions)
{
    /// <summary>
    /// The length a rule's clip condition needs. A clip cut from it holds at least five minutes
    /// before the action.
    /// </summary>
    public const int ReadyMinutes = 10;

    /// <summary>The <c>op</c> of the WebSocket message that carries a report.</summary>
    public const string Op = "clips";

    /// <summary>
    /// Whether this device can be counted on to send a clip for its owner's actions: Clips on,
    /// recording now, the ten-minute setting, and the switch ticked.
    /// </summary>
    public bool IsReady => On && Recording && Minutes >= ReadyMinutes && SendForActions;
}
