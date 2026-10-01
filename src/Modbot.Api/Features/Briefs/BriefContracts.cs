namespace Modbot.Api.Features.Briefs;

/// <summary>Asks for a brief about one person. Give exactly one of the three ids.</summary>
/// <param name="VRChatUserId">Taken as sent, never checked for shape (foundation §3.1.1).</param>
/// <param name="TimeZone">
/// The IANA name of the reader's time zone, such as <c>Europe/London</c>, so the times in the brief
/// read the way the rest of the app shows them. UTC when left out or not a zone.
/// </param>
public sealed record PersonBriefRequest(
    string? VRChatUserId = null,
    string? DiscordUserId = null,
    Guid? AccountId = null,
    string? TimeZone = null);

/// <summary>Asks for a brief about one instance.</summary>
/// <param name="TimeZone">As for <see cref="PersonBriefRequest.TimeZone"/>.</param>
public sealed record InstanceBriefRequest(string? TimeZone = null);

/// <summary>
/// A brief: a summary, written by AI, of the audit log entries an instance's or a person's Activity
/// tab shows the asker (AI chat design §14).
/// </summary>
/// <param name="Text">
/// The brief, lines of text with the entry ids each rests on in square brackets. Null when nothing
/// was recorded, in which case the model was not asked.
/// </param>
/// <param name="Sources">
/// The entry ids the text cites that were among the entries it was given, in the order first
/// cited. An id the model wrote that it was not given is left out, so a link is only ever drawn to
/// an entry that was there.
/// </param>
/// <param name="Entries">How many audit log entries the model was given.</param>
/// <param name="Newest">True when there were more entries than that and these were the newest.</param>
/// <param name="BuiltFrom">
/// What the brief was built from, in one line: "Written by AI from 42 audit log entries, …". Kept
/// with the text when the brief is saved as a note.
/// </param>
/// <param name="CallId">The call log row that answered, which a note saved from this brief names.</param>
/// <param name="Model">The model that wrote it.</param>
public sealed record BriefView(
    string? Text,
    IReadOnlyList<long> Sources,
    int Entries,
    bool Newest,
    string? BuiltFrom,
    Guid? CallId,
    string? Model);
