using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Putting evidence on a case file, taking it off, destroying it, and writing down who looked at
/// it or copied it.
/// </summary>
/// <remarks>
/// <para>
/// Every change here commits together with the fact that says who made it, the way a case file's
/// own edits do: an attachment with no record of who put it there is the failure the audit log
/// exists to prevent. The facts are about the case file, not about the person on it -- see the
/// note above <see cref="FactType.EvidenceAttached"/> -- and each names the case file, the file (its
/// hash and the name it was put on under) and who did it.
/// </para>
/// <para>
/// A file is stored once and can be on several case files. Taking it off one case file ends that
/// case file's hold and touches nothing else; destroying it needs every case file to have let go.
/// </para>
/// </remarks>
public sealed class EvidenceAttachments
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly AccountFacts _facts;
    private readonly EvidenceViewThrottle _throttle;

    public EvidenceAttachments(
        ModbotContext db,
        IModbotClock clock,
        AccountFacts facts,
        EvidenceViewThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(throttle);

        _db = db;
        _clock = clock;
        _facts = facts;
        _throttle = throttle;
    }

    // ── The case file a change is for ──────────────────────────────────────────────────────

    /// <summary>
    /// The case file a file may be put on or taken off by this caller: it exists, it has not been
    /// withdrawn, and the caller wrote it or may ban.
    /// </summary>
    /// <exception cref="CaseFileRefused">With the status to answer.</exception>
    public async Task<CaseFile> RequireEditableAsync(string? caseId, Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var row = await FindCaseAsync(caseId, ct);
        CaseFileService.RequireEditable(row, caller);

        return row;
    }

    /// <summary>The case file with this id, for a change that does not need it to be editable.</summary>
    /// <exception cref="CaseFileRefused">404 when there is none, or when the id is not one.</exception>
    public async Task<CaseFile> FindCaseAsync(string? caseId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseId))
            throw new CaseFileRefused(400, "Say which case file to use.");

        if (!Guid.TryParse(caseId, out var id)
            || await _db.CaseFiles.FirstOrDefaultAsync(c => c.Id == id, ct) is not { } row)
        {
            throw new CaseFileRefused(404, "No such case file.");
        }

        return row;
    }

    // ── Putting on and taking off ──────────────────────────────────────────────────────────

    /// <summary>
    /// Puts a file on a case file. Does nothing, and writes nothing, when it is already on.
    /// </summary>
    /// <param name="clip">
    /// The saved clip this file was matched to, when it was attached as one. The caller has already
    /// made the commit check the bytes against the clip's SHA-256. Here, in the same transaction as
    /// the hold and its fact, the file is marked as captured with where and when the clip was saved
    /// and whose device reported it, and the <see cref="FactType.EvidenceAttached"/> fact carries the
    /// same. A file the case file already holds is left as it is: the case file never offers a clip
    /// it already holds.
    /// </param>
    /// <returns>Whether it was newly put on.</returns>
    public async Task<bool> AttachAsync(
        CaseFile caseFile, CommitResult committed, Actor actor, CancellationToken ct, SavedClip? clip = null)
    {
        ArgumentNullException.ThrowIfNull(caseFile);
        ArgumentNullException.ThrowIfNull(committed);

        var caseId = caseFile.Id.ToString();
        var hash = committed.Hash.Hex;

        if (await IsOnAsync(caseId, hash, ct))
            return false;

        var name = Cleaned(committed.FileName);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Saved with the hold below, so the file is never marked as a clip without the fact that
        // says who attached it as one, nor the other way round.
        if (clip is not null)
            await MarkAsClipAsync(hash, clip, ct);

        _db.EvidenceAttachments.Add(new EvidenceAttachment
        {
            Hash = hash,
            CaseId = caseId,
            AttachedAt = _clock.UtcNow,
            AttachedByUserId = actor.Id,
            AttachedByName = actor.Username,
            FileName = name,
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two people put the same file on the same case file in the same instant, and the other
            // one won. The file is on, which is what this one wanted; anything else is a real
            // failure and goes on up.
            await transaction.RollbackAsync(ct);
            _db.ChangeTracker.Clear();

            if (await IsOnAsync(caseId, hash, ct))
                return false;

            throw;
        }

        await _facts.RecordAsync(
            FactType.EvidenceAttached,
            caseId,
            actor,
            About(caseFile, hash, name, new JsonObject
            {
                ["byteSize"] = committed.ByteSize,
                ["contentType"] = committed.ContentType,
                ["description"] = clip is null
                    ? $"{actor.Username} attached {name ?? "a file"} to a case file"
                    : $"{actor.Username} attached a clip saved on {clip.SavedBy}'s PC to a case file",
                ["clip"] = clip is null ? null : new JsonObject
                {
                    ["clipId"] = clip.Id,
                    ["savedAt"] = clip.SavedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["worldId"] = clip.WorldId,
                    ["instanceId"] = clip.InstanceId,
                    ["savedById"] = clip.SavedById,
                    ["savedByUserId"] = clip.SavedByUserId.ToString(),
                    ["savedBy"] = clip.SavedBy,
                    ["deviceId"] = clip.DeviceId?.ToString(),
                },
            }),
            ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Marks the file's own record as this saved clip — captured, and where, when and by whose
    /// device — for the caller's next save. Once: a file already marked keeps what it was first
    /// marked with.
    /// </summary>
    private async Task MarkAsClipAsync(string hash, SavedClip clip, CancellationToken ct)
    {
        var blob = await _db.EvidenceBlobs.FirstOrDefaultAsync(b => b.Hash == hash, ct);
        if (blob is null || blob.ClipSavedAt is not null)
            return;

        blob.Origin = EvidenceOriginKind.Captured;
        blob.ClipSavedAt = clip.SavedAt;
        blob.ClipWorldId = Cut(clip.WorldId, 128);
        blob.ClipInstanceId = Cut(clip.InstanceId, 256);
        blob.ClipSavedById = Cut(clip.SavedById, 128);
        blob.ClipSavedByUserId = clip.SavedByUserId;
        blob.ClipSavedByName = Cut(clip.SavedBy, 128);
        blob.ClipDeviceId = clip.DeviceId;
    }

    private static string? Cut(string? text, int length)
        => text is null ? null : text.Length > length ? text[..length] : text;

    /// <summary>Takes a file off a case file, keeping the record that it was there.</summary>
    /// <exception cref="CaseFileRefused">404 when the file is not on this case file.</exception>
    public async Task TakeOffAsync(CaseFile caseFile, string hash, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caseFile);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        if (await TakeOffInsideAsync(caseFile, hash, actor, ct) is null)
            throw new CaseFileRefused(404, "That file is not on this case file.");

        await transaction.CommitAsync(ct);
    }

    /// <summary>The take-off and its fact, in the caller's transaction. Null when the file was not on.</summary>
    private async Task<EvidenceAttachment?> TakeOffInsideAsync(
        CaseFile caseFile, string hash, Actor actor, CancellationToken ct)
    {
        var caseId = caseFile.Id.ToString();

        var row = await _db.EvidenceAttachments
            .FirstOrDefaultAsync(a => a.CaseId == caseId && a.Hash == hash && a.TakenOffAt == null, ct);

        if (row is null)
            return null;

        row.TakenOffAt = _clock.UtcNow;
        row.TakenOffByUserId = actor.Id;
        row.TakenOffByName = actor.Username;

        await _db.SaveChangesAsync(ct);

        var name = await NameOfAsync(row, ct);

        await _facts.RecordAsync(
            FactType.EvidenceDetached,
            caseId,
            actor,
            About(caseFile, hash, name, new JsonObject
            {
                ["description"] = $"{actor.Username} took {name ?? "a file"} off a case file",
            }),
            ct);

        return row;
    }

    // ── Destroying ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Destroys a file's bytes. Done from a case file, that case file lets go of it first; any
    /// other case file still holding it stops the destroy and is named.
    /// </summary>
    /// <param name="caseId">The case file it is being destroyed from, when it is being destroyed from one.</param>
    /// <exception cref="CaseFileRefused">404 when that case file does not exist.</exception>
    public async Task<EvidenceDestroyResponse> DestroyAsync(
        EvidenceHash hash,
        string? caseId,
        Actor actor,
        string reason,
        EvidenceDestroyer destroyer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destroyer);

        var key = hash.Hex;
        var blob = await _db.EvidenceBlobs.AsNoTracking().FirstOrDefaultAsync(b => b.Hash == key, ct)
            ?? throw new CaseFileRefused(404, "No evidence with that content address has been stored here.");

        var from = string.IsNullOrWhiteSpace(caseId) ? null : await FindCaseAsync(caseId, ct);

        // A destroy that already finished is not done twice: the first one keeps its date, its
        // person and its reason, and the log keeps one line for it. "Finished" means marked, and the
        // mark and its line are one commit below, so a file that is marked always has its line.
        if (blob is { IsDestroyed: true })
            return new EvidenceDestroyResponse(true, [], "These bytes were already destroyed.");

        // The name as the case file knew it, read now: the take-off below does not change it, but
        // the file's record is the fallback.
        var name = await NameOnAsync(key, from?.Id.ToString(), blob?.FileName, ct);

        // The order is the point. One: the case file lets go, with its line, in its own commit.
        // Two: the bytes go; deleting bytes that are already gone is fine in every store. Three:
        // the file is marked destroyed and the destroy's line is written in ONE commit. A crash
        // after two leaves the file unmarked, so trying again runs two and three and finishes; a
        // crash cannot leave a file marked destroyed with no line saying who did it.
        var result = await destroyer.DestroyAsync(
            hash,
            actor.Username,
            reason,
            ct,
            ignoreReports: from is null ? null : [from.Id.ToString()],
            beforeDelete: async token =>
            {
                if (from is null)
                    return;

                await using var transaction = await _db.Database.BeginTransactionAsync(token);
                await TakeOffInsideAsync(from, key, actor, token);
                await transaction.CommitAsync(token);
            },
            markWithin: async (mark, token) =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                // Only the call that marked it writes the line: a second destroy racing this one
                // finds it marked and says nothing.
                if (await mark(token))
                {
                    var data = new JsonObject
                    {
                        ["reason"] = reason,
                        ["byteSize"] = blob?.ByteSize,
                        ["contentType"] = blob?.ContentType,
                        ["description"] = $"{actor.Username} destroyed {name ?? "a file"}: {reason}",
                    };

                    data["hash"] = key;
                    data["fileName"] = name;

                    if (from is not null)
                    {
                        data["caseId"] = from.Id.ToString();
                        data["userId"] = from.UserId;
                    }

                    await _facts.RecordAsync(FactType.EvidenceDestroyed, from?.Id.ToString() ?? key, actor, data, token);
                }

                await transaction.CommitAsync(token);
            });

        if (!result.Destroyed)
        {
            return new EvidenceDestroyResponse(
                false,
                result.BlockedByReports,
                await BlockedMessageAsync(result.BlockedByReports, ct));
        }

        return new EvidenceDestroyResponse(true, [], "The bytes were destroyed.");
    }

    // ── Looking and downloading ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes down that somebody looked at, or downloaded, a file.
    /// </summary>
    /// <param name="caseId">
    /// The case file page it was opened from, when the request says. Trusted only when the file is
    /// or was on that case file; otherwise the case file that holds it now is named, and none when
    /// nothing does.
    /// </param>
    /// <param name="download">Written every time. A look is written at most once per person per file per ten minutes.</param>
    public async Task RecordAccessAsync(
        string hash, string? caseId, Actor? actor, bool download, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var person = actor?.Id ?? Guid.Empty;

        if (!download && !_throttle.TryClaim(person, hash, now))
            return;

        try
        {
            await WriteAccessAsync(hash, caseId, actor, download, ct);
        }
        catch
        {
            // The line was not written, so the slot it took must not silence the next look.
            if (!download)
                _throttle.Release(person, hash, now);

            throw;
        }
    }

    private async Task WriteAccessAsync(
        string hash, string? caseId, Actor? actor, bool download, CancellationToken ct)
    {
        var rows = await _db.EvidenceAttachments.AsNoTracking()
            .Where(a => a.Hash == hash)
            .OrderByDescending(a => a.AttachedAt)
            .ToListAsync(ct);

        var named = (caseId is { Length: > 0 } ? rows.FirstOrDefault(a => a.CaseId == caseId) : null)
            ?? rows.FirstOrDefault(a => a.IsOn);

        var blobName = await _db.EvidenceBlobs.AsNoTracking()
            .Where(b => b.Hash == hash)
            .Select(b => b.FileName)
            .FirstOrDefaultAsync(ct);

        var name = named?.FileName ?? blobName;

        var data = new JsonObject
        {
            ["hash"] = hash,
            ["fileName"] = name,
        };

        if (named is not null)
        {
            data["caseId"] = named.CaseId;

            if (Guid.TryParse(named.CaseId, out var id)
                && await _db.CaseFiles.AsNoTracking().Where(c => c.Id == id).Select(c => c.UserId).FirstOrDefaultAsync(ct) is { } userId)
            {
                data["userId"] = userId;
            }
        }

        var who = actor?.Username ?? "Somebody";
        data["description"] = download
            ? $"{who} downloaded {name ?? "a file"}"
            : $"{who} viewed {name ?? "a file"}";

        await _facts.RecordAsync(
            download ? FactType.EvidenceDownloaded : FactType.EvidenceViewed,
            named?.CaseId ?? hash,
            actor,
            data,
            ct);
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────

    private async Task<bool> IsOnAsync(string caseId, string hash, CancellationToken ct)
        => await _db.EvidenceAttachments.AsNoTracking()
            .AnyAsync(a => a.CaseId == caseId && a.Hash == hash && a.TakenOffAt == null, ct);

    /// <summary>The name a file has on a case file: the one it was put on under, else the uploader's.</summary>
    private async Task<string?> NameOfAsync(EvidenceAttachment row, CancellationToken ct)
        => row.FileName
           ?? await _db.EvidenceBlobs.AsNoTracking()
               .Where(b => b.Hash == row.Hash)
               .Select(b => b.FileName)
               .FirstOrDefaultAsync(ct);

    private async Task<string?> NameOnAsync(string hash, string? caseId, string? blobName, CancellationToken ct)
    {
        var named = caseId is null
            ? null
            : await _db.EvidenceAttachments.AsNoTracking()
                .Where(a => a.Hash == hash && a.CaseId == caseId)
                .OrderByDescending(a => a.AttachedAt)
                .Select(a => a.FileName)
                .FirstOrDefaultAsync(ct);

        return named ?? blobName;
    }

    /// <summary>The case files in the way of a destroy, by the person each is about.</summary>
    private async Task<string> BlockedMessageAsync(IReadOnlyList<string> caseIds, CancellationToken ct)
    {
        var ids = caseIds.Select(c => Guid.TryParse(c, out var g) ? g : (Guid?)null).OfType<Guid>().ToList();

        var people = await (
                from c in _db.CaseFiles.AsNoTracking()
                join u in _db.VRChatUsers.AsNoTracking() on c.UserId equals u.UserId into names
                from u in names.DefaultIfEmpty()
                where ids.Contains(c.Id)
                select u != null && u.DisplayName != null ? u.DisplayName : c.UserId)
            .ToListAsync(ct);

        return people.Count switch
        {
            0 => "This file is still on another case file. Take it off there first.",
            1 => $"This file is still on the case file for {people[0]}. Take it off there first.",
            _ => $"This file is still on {people.Count} other case files: for {string.Join(", ", people)}. "
                 + "Take it off there first.",
        };
    }

    private static JsonObject About(CaseFile caseFile, string hash, string? name, JsonObject data)
    {
        data["caseId"] = caseFile.Id.ToString();
        data["userId"] = caseFile.UserId;
        data["hash"] = hash;
        data["fileName"] = name;
        return data;
    }

    /// <summary>A file name reduced to something a log line can carry: what the person typed, trimmed and cut to length.</summary>
    private static string? Cleaned(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }
}
