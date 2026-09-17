namespace Modbot.Companion.LogReading;

/// <summary>
/// One record from VRChat's own output log, split into its envelope and its message.
/// </summary>
/// <param name="Timestamp">
/// The wall-clock time VRChat wrote, in the machine's local time with <em>no offset recorded</em>
/// — see <see cref="VRChatLogLineParser"/>. Deliberately <see cref="DateTimeKind.Unspecified"/>:
/// pretending it is UTC is how presence history silently ends up hours out.
/// </param>
/// <param name="Level">VRChat's own level word: <c>Debug</c>, <c>Warning</c>, <c>Error</c>.</param>
/// <param name="Tag">The bracketed subsystem tag, or <c>null</c> when the line carries none.</param>
/// <param name="Message">Everything after the tag. Never transmitted anywhere — see the note on
/// <see cref="VRChatLogLineParser"/>.</param>
public readonly record struct VRChatLogLine(
    DateTime Timestamp,
    string Level,
    string? Tag,
    string Message);
