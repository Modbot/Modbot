namespace Modbot.Api.Features.Notes;

/// <summary>What a moderator wants written down about somebody.</summary>
/// <param name="UserId">
/// The person the note is about. Opaque: taken as sent, never parsed and never checked for shape
/// (foundation §3.1.1). Required — a note about nobody is not a note.
/// </param>
/// <param name="Platform">
/// <c>VRChat</c> or <c>Discord</c>, saying which of the person's accounts the id belongs to.
/// Defaults to VRChat.
/// </param>
/// <param name="Text">The moderator's own words. Stored and shown as text, never as markup.</param>
public sealed record WriteNoteRequest(string UserId, string? Platform = null, string Text = "");

/// <summary>One note, as it is read back.</summary>
/// <param name="Id">
/// The id of the fact the note is stored as. It is the note's only id, because the fact is the
/// note (notes design §3).
/// </param>
/// <param name="WrittenAt">When it was written, on Modbot's clock — or the date an import carried.</param>
/// <param name="Text">What it says, verbatim.</param>
/// <param name="SubjectPlatform">Which account it is about.</param>
/// <param name="SubjectId">The person it is about.</param>
/// <param name="AuthorAccountId">
/// The Modbot account that wrote it, when Modbot wrote it. Null for a note carried in from another
/// system, which has no account behind it.
/// </param>
/// <param name="AuthorName">Their name as it stood when they wrote it, when one was recorded.</param>
/// <param name="Imported">True when the note came in from another system rather than being written here.</param>
/// <param name="TakenBack">True when somebody has taken it back. It still exists; it no longer counts.</param>
/// <param name="TakenBackAt">When that happened.</param>
/// <param name="TakenBackByName">Who did it.</param>
/// <param name="CanTakeBack">
/// Whether the person reading may take this one back — they wrote it, or they may write notes.
/// Always false for one already taken back.
/// </param>
public sealed record NoteView(
    long Id,
    DateTimeOffset WrittenAt,
    string Text,
    string SubjectPlatform,
    string SubjectId,
    Guid? AuthorAccountId,
    string? AuthorName,
    bool Imported,
    bool TakenBack,
    DateTimeOffset? TakenBackAt,
    string? TakenBackByName,
    bool CanTakeBack);

/// <summary>A person's notes, newest first.</summary>
/// <param name="Notes">
/// Notes that were taken back are in here too, marked. A note that was written and withdrawn is a
/// different thing from one nobody ever wrote, and a list that hid them would blur the two.
/// </param>
/// <param name="Standing">How many of them still stand — the number worth putting on a tab.</param>
/// <param name="CanWrite">Whether the person reading may add one.</param>
public sealed record NoteListResponse(IReadOnlyList<NoteView> Notes, int Standing, bool CanWrite);
