using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Cases;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Notes;

/// <summary>Why a note could not be written or taken back, with the status to answer with.</summary>
public sealed class NoteRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Notes: one moderator's own words about a person, written in Modbot and read back beside
/// everything else recorded about them (M4 §2, notes design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A note is a fact and nothing else.</strong> There is no note table. A fact already
/// carries who wrote it, who it is about and when, which is nearly all of a note; the importer
/// has been writing <see cref="FactType.NoteAdded"/> since the import feature shipped, and giving
/// notes written here a row of their own would have made a note written in Modbot and a note
/// carried in from an old bot two different things that one list had to pretend were one. It also
/// keeps erasure honest: a purge deletes a person's facts, so it deletes their notes, with no
/// second table for somebody to forget (notes design §3).
/// </para>
/// <para>
/// <strong>Nothing is edited and nothing is deleted.</strong> The fact log is append-only (M4
/// §10). A note that should not stand is <em>taken back</em>, which is a second fact naming the
/// first; both stay in the audit log, so "this was written, and then withdrawn" is answerable, and
/// a note whose wording is wrong is answered the way a ban is — with another entry, not by
/// rewriting the first one.
/// </para>
/// <para>
/// <strong>Reading is the audit log's permission, writing is its own.</strong> The note is a fact
/// in the Moderation category, so <c>ViewAuditLog</c> already decides who may read it; a second,
/// looser door onto the same rows would make <see cref="Features.Audit.AuditVisibility"/> a
/// suggestion. Writing is <see cref="ModbotPermissions.WriteNotes"/>, because putting one
/// moderator's words about a named person into the log everybody reads is not implied by being
/// allowed to read it.
/// </para>
/// </remarks>
public sealed class NoteService
{
    /// <summary>
    /// The longest a note may be.
    /// </summary>
    /// <remarks>
    /// A note is a remark, not a write-up: the write-up of a ban is the case file, which allows
    /// twenty thousand characters for exactly that. Two thousand is what a withdrawal note on a
    /// case file already allows, and a cap keeps one person's essay out of a list ten other people
    /// have to scan before acting.
    /// </remarks>
    public const int MaxTextLength = 2000;

    /// <summary>How many notes one read returns. Newest first.</summary>
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter? _facts;
    private readonly EventPartitionMaintainer? _partitions;

    public NoteService(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter? facts = null,
        EventPartitionMaintainer? partitions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
    }

    // ── Writing ────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes one note about one person.</summary>
    public async Task<NoteView> WriteAsync(WriteNoteRequest request, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.Has(ModbotPermissions.WriteNotes))
            throw new NoteRefused(403, "You do not have permission to write notes.");

        // Taken as sent and never checked for shape: a legacy VRChat id follows no structure
        // (foundation §3.1.1). All that is refused is nobody at all.
        var userId = (request.UserId ?? string.Empty).Trim();

        if (userId.Length == 0)
            throw new NoteRefused(400, "Pick a person first: Modbot was given no id.");

        var platform = PlatformOf(request.Platform);
        var text = (request.Text ?? string.Empty).Trim();

        if (text.Length == 0)
            throw new NoteRefused(400, "A note needs something in it.");

        if (text.Length > MaxTextLength)
            throw new NoteRefused(400, $"That note is too long (at most {MaxTextLength} characters).");

        var (facts, partitions) = Writer();
        var now = _clock.UtcNow;

        await partitions.EnsureForAsync(now, ct);

        var written = await facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.NoteAdded,
                OccurredAt = now,
                SubjectPlatform = platform,
                SubjectId = userId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = new JsonObject
                {
                    ["text"] = text,
                    ["actorDisplayName"] = caller.Username,

                    // The same words again, under the key every reader of a timeline already
                    // looks in: the audit-log row, the Discord card and the AI tools all read
                    // `description`, and the Discord card escapes it before it is posted
                    // (CardText.EscapeText), which is how a note reaches a channel as typed
                    // rather than as markup.
                    ["description"] = text,
                },
            },
            ct);

        await _db.SaveChangesAsync(ct);

        return new NoteView(
            written.Id,
            now,
            text,
            platform.ToString(),
            userId,
            caller.UserId,
            caller.Username,
            Imported: false,
            TakenBack: false,
            TakenBackAt: null,
            TakenBackByName: null,
            CanTakeBack: true);
    }

    // ── Taking one back ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records that a note no longer stands. The note itself is untouched; both facts remain.
    /// </summary>
    /// <remarks>
    /// Open to whoever wrote it as well as to anyone who may write notes, the same way a case file
    /// can be corrected by its author or by anyone who may ban: a volunteer who has since lost the
    /// permission can still take back something they wrote, and a note left behind by somebody who
    /// has moved on is not stuck there forever.
    /// </remarks>
    public async Task<NoteView> TakeBackAsync(long noteFactId, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var note = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == noteFactId && e.Type == FactType.NoteAdded, ct)
            ?? throw new NoteRefused(404, "No such note.");

        var mine = note.ActorId is not null
            && string.Equals(note.ActorId, caller.UserId.ToString(), StringComparison.Ordinal);

        if (!caller.Has(ModbotPermissions.WriteNotes) && !mine)
            throw new NoteRefused(403, "You do not have permission to take back this note.");

        var existing = await TakeBacksAsync(note.SubjectPlatform, note.SubjectId, [noteFactId], ct);

        if (existing.TryGetValue(noteFactId, out var already))
            return Shape(note, already, caller);

        var (facts, partitions) = Writer();
        var now = _clock.UtcNow;

        await partitions.EnsureForAsync(now, ct);

        await facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.NoteTakenBack,
                OccurredAt = now,
                SubjectPlatform = note.SubjectPlatform,
                SubjectId = note.SubjectId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = caller.UserId.ToString(),
                Source = FactSource.Manual,
                Data = new JsonObject
                {
                    ["noteFactId"] = noteFactId,
                    ["actorDisplayName"] = caller.Username,

                    // The note's words again, so one entry in the log still answers what was
                    // taken back without the reader having to find the note it names.
                    ["text"] = TextOf(note.Data),
                    ["description"] = $"Note taken back by {caller.Username}: {TextOf(note.Data)}",
                },
            },
            ct);

        await _db.SaveChangesAsync(ct);

        return Shape(note, new TakeBack(now, caller.Username), caller);
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────

    /// <summary>One person's notes, newest first.</summary>
    public async Task<NoteListResponse> ListAsync(
        string userId, string? platform, int limit, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var subject = (userId ?? string.Empty).Trim();

        if (subject.Length == 0)
            throw new NoteRefused(400, "Say which person's notes to read.");

        var of = PlatformOf(platform);
        var take = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        var notes = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.NoteAdded
                && e.SubjectPlatform == of
                && e.SubjectId == subject)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .ToListAsync(ct);

        var takenBack = await TakeBacksAsync(of, subject, notes.Select(n => n.Id).ToList(), ct);

        var views = notes
            .Select(n => Shape(n, takenBack.GetValueOrDefault(n.Id), caller))
            .ToList();

        return new NoteListResponse(
            views,
            views.Count(v => !v.TakenBack),
            caller.Has(ModbotPermissions.WriteNotes));
    }

    // ── Shaping ────────────────────────────────────────────────────────────────────────────

    /// <summary>A note's withdrawal: when, and by whom.</summary>
    private readonly record struct TakeBack(DateTimeOffset At, string? ByName);

    /// <summary>
    /// Which of these notes have been taken back.
    /// </summary>
    /// <remarks>
    /// Read from the take-back facts rather than from a column, because there is no column: the
    /// pair of facts is the whole record. The id is matched inside the payload, which is why it is
    /// written there as a number and read back as either — a fact written by an older Modbot, or
    /// by hand, may carry it as text.
    /// <para>
    /// Narrowed to the one person first, so this reads their handful of facts on
    /// <c>ix_modbot_event_subject</c> rather than every take-back the deployment has ever written.
    /// A take-back is always about the same person as the note it names, so nothing is missed.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<long, TakeBack>> TakeBacksAsync(
        FactPlatform platform, string subjectId, IReadOnlyCollection<long> noteIds, CancellationToken ct)
    {
        var found = new Dictionary<long, TakeBack>();

        if (noteIds.Count == 0)
            return found;

        var wanted = noteIds.ToHashSet();

        var candidates = await _db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.NoteTakenBack
                && e.SubjectPlatform == platform
                && e.SubjectId == subjectId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        foreach (var fact in candidates)
        {
            if (NoteIdOf(fact.Data) is not { } id || !wanted.Contains(id))
                continue;

            // The first take-back of a note is the one that counts; a second changes nothing.
            found.TryAdd(id, new TakeBack(fact.OccurredAt, NameOf(fact.Data)));
        }

        return found;
    }

    private static NoteView Shape(ModbotEvent note, TakeBack? takenBack, Caller caller)
    {
        var author = Guid.TryParse(note.ActorId, out var accountId) ? accountId : (Guid?)null;
        var mine = author is { } id && id == caller.UserId;

        return new NoteView(
            note.Id,
            note.OccurredAt,
            TextOf(note.Data),
            note.SubjectPlatform.ToString(),
            note.SubjectId,
            author,
            NameOf(note.Data),
            Imported: ImportedFrom(note.Data),
            TakenBack: takenBack is not null,
            TakenBackAt: takenBack?.At,
            TakenBackByName: takenBack?.ByName,
            CanTakeBack: takenBack is null && (caller.Has(ModbotPermissions.WriteNotes) || mine));
    }

    /// <summary>
    /// What a note says.
    /// </summary>
    /// <remarks>
    /// Notes written here put the words under <c>text</c>. A note carried in from another system
    /// carries whatever that file's <c>data</c> held (import design §2), so the other two keys a
    /// note plausibly arrives under are tried before giving up — an imported note with no words
    /// anywhere is still a note that was written, and reads as an empty one rather than vanishing.
    /// </remarks>
    private static string TextOf(string data)
        => Read(data, "text") ?? Read(data, "note") ?? Read(data, "description") ?? string.Empty;

    private static string? NameOf(string data) => Read(data, "actorDisplayName");

    private static bool ImportedFrom(string data) => Read(data, "importId") is not null;

    private static string? Read(string data, string key)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            using var document = JsonDocument.Parse(data);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? NoteIdOf(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            using var document = JsonDocument.Parse(data);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!document.RootElement.TryGetProperty("noteFactId", out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The platform an id belongs to. Anything Modbot does not recognise is refused rather than
    /// guessed at: a note filed under the wrong platform is a note about somebody else.
    /// </summary>
    private static FactPlatform PlatformOf(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return FactPlatform.VRChat;

        if (Enum.TryParse<FactPlatform>(platform, ignoreCase: true, out var parsed)
            && parsed is FactPlatform.VRChat or FactPlatform.Discord)
        {
            return parsed;
        }

        throw new NoteRefused(400, "A note is about a person on VRChat or on Discord.");
    }

    /// <summary>
    /// The fact log, or a refusal. A host that mapped the API without it can still read notes; it
    /// simply cannot write one, and says so rather than failing mid-request.
    /// </summary>
    private (IFactWriter Facts, EventPartitionMaintainer Partitions) Writer()
        => _facts is null || _partitions is null
            ? throw new NoteRefused(503, "This deployment is not set up to write notes.")
            : (_facts, _partitions);
}
