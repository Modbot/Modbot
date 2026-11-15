namespace Modbot.Shared.Clips;

/// <summary>
/// What a companion tells its server about its Clips settings, kept where both read it
/// (automatic roles, 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// Sent over the live connection the companion already holds: once when it opens, and again
/// whenever one of the five values changes, as <c>{"op":"clips","on":...}</c> on the WebSocket and
/// as the <c>clipsOn</c>, <c>clipsRecording</c>, <c>clipsMinutes</c>, <c>clipsSendForActions</c> and
/// <c>clipsDiscordSound</c> query values on a long poll. A companion older than the fifth value
/// leaves it out, and the server counts it as off. Nothing else about the clips is told: not a
/// file, not a name, not a folder.
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
/// <param name="DiscordSound">"Discord's sound too" is ticked. The box alone counts; Discord need not be running.</param>
public sealed record ClipSettingsReport(bool On, bool Recording, int Minutes, bool SendForActions, bool DiscordSound)
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
    /// recording now, the ten-minute setting, the switch ticked, and Discord's sound ticked.
    /// </summary>
    public bool IsReady => On && Recording && Minutes >= ReadyMinutes && SendForActions && DiscordSound;
}
