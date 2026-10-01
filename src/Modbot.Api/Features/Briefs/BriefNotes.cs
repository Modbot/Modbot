namespace Modbot.Api.Features.Briefs;

/// <summary>
/// The payload keys a brief's lookup entry carries, so a person's brief can later be saved as a note
/// about that person and nobody else (AI chat design §14.6, <c>NoteService.SaveBriefAsync</c>).
/// </summary>
public static class BriefNotes
{
    /// <summary>Which brief read the entries: <see cref="PersonBrief"/> or <see cref="InstanceBrief"/>.</summary>
    public const string KindKey = "brief";

    public const string PersonBrief = "person";

    public const string InstanceBrief = "instance";

    /// <summary>The call log row the brief answered on. Only on an entry whose brief answered.</summary>
    public const string CallKey = "aiCallId";

    /// <summary>The line saying what the brief was built from, as it was shown.</summary>
    public const string BuiltFromKey = "builtFrom";
}
