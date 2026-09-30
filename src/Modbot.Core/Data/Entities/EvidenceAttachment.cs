namespace Modbot.Core.Data.Entities;

/// <summary>
/// One case file holding one evidence file: when it was put on, by whom, and when it was taken
/// off.
/// </summary>
/// <remarks>
/// <para>
/// A file is stored once (<see cref="EvidenceBlob"/>) and can be held by any number of case files.
/// This row is the holding. It used to be a single column on the file's own row, so a second case
/// file that attached the same screenshot found it "attached" and never showed it, and nothing
/// could ever clear the column: a file that had once been on a case file could never be destroyed,
/// because "is anything still holding this?" never came back empty.
/// </para>
/// <para>
/// <strong>Taking a file off keeps the row.</strong> <see cref="TakenOffAt"/> is set and the row
/// stays, so the case file can say "photo.png was taken off by sam on 29 September" instead of
/// looking as if it never had the file. Putting the same file back is a new row: the old one is
/// history and stays as it was.
/// </para>
/// <para>
/// Who did it is kept as an account id and as the username at the time, the same pair a case file
/// keeps for its author, so a renamed or deleted account does not turn the record into a blank.
/// </para>
/// </remarks>
public class EvidenceAttachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The file: the hash of <see cref="EvidenceBlob"/>.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The case file that holds it. Kept as the id's text, the way the file's own record used to.</summary>
    public string CaseId { get; set; } = string.Empty;

    public DateTimeOffset AttachedAt { get; set; }

    /// <summary>The account that put it on. Null for a file Modbot put there itself.</summary>
    public Guid? AttachedByUserId { get; set; }

    public string? AttachedByName { get; set; }

    /// <summary>
    /// What the person who put it on called it. Display only and hostile input, like the file's own
    /// name; it is here because the same bytes can arrive under two names on two case files.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>When it was taken off this case file. Null while it is on.</summary>
    public DateTimeOffset? TakenOffAt { get; set; }

    public Guid? TakenOffByUserId { get; set; }

    public string? TakenOffByName { get; set; }

    /// <summary>Whether the case file still holds the file.</summary>
    public bool IsOn => TakenOffAt is null;
}
