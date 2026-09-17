using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Users;

/// <summary>
/// One person's profile fields as they stood at a moment. A null field is one Modbot did not
/// know at that moment, or one that was empty; the two cannot be told apart from the facts.
/// </summary>
public sealed record ProfileFields(
    string? DisplayName,
    string? Bio,
    string? StatusDescription,
    string? Pronouns,
    string? AvatarImageUrl,
    string? AvatarThumbnailUrl,
    string? ProfilePictureUrl,
    string? DateJoined,
    IReadOnlyList<string> Tags,
    string? AgeVerificationStatus,
    bool? AgeVerified);

/// <summary>
/// The profile as it stood after one recorded change.
/// </summary>
/// <param name="FactId">The fact that recorded the change, so the audit log and the popup can meet on it.</param>
/// <param name="At">When the change was noticed, or the start of the window it happened in.</param>
/// <param name="Before">The end of that window, or null when the time is exact.</param>
/// <param name="Changed">The fields this change touched, under VRChat's own names.</param>
/// <param name="Baseline">True for the first sighting: nothing changed, this is where the record begins.</param>
/// <param name="Current">True for the newest version, which is the profile as stored now.</param>
public sealed record ProfileVersion(
    long FactId,
    DateTimeOffset At,
    DateTimeOffset? Before,
    string Source,
    IReadOnlyList<string> Changed,
    bool Baseline,
    bool Current,
    ProfileFields Profile);

/// <param name="Known">False when Modbot has no row for this id at all.</param>
/// <param name="Versions">Newest first. Empty when no profile has ever been read.</param>
public sealed record ProfileHistory(
    string UserId,
    bool Known,
    IReadOnlyList<ProfileVersion> Versions,
    DateTimeOffset Now);

/// <param name="PublicProfile">The public profile as VRChat last returned it, or null when never read.</param>
/// <param name="User">The full user object as VRChat last returned it, minus what Modbot never keeps, or null when never read.</param>
public sealed record RawProfile(
    string UserId,
    JsonNode? PublicProfile,
    DateTimeOffset? PublicProfileReadAt,
    JsonNode? User,
    DateTimeOffset? UserReadAt);

/// <summary>
/// A person's profile over time, replayed from the facts, and the raw bodies VRChat sent.
/// </summary>
/// <remarks>
/// <para>
/// There is no versions table and none is needed. The profile sync writes a first-seen fact with
/// a baseline and a changed fact with <c>{field: {old, new}}</c> for every later change (user
/// profile sync design §3), and the row holds the profile as it stands now. Walking the changes
/// backwards from the row, putting each <c>old</c> back, gives the profile as it stood after
/// every earlier change -- so "what did they look like on the day they were banned" is answered
/// from what is already written down, without a second copy of anything.
/// </para>
/// <para>
/// The walk starts from the row rather than forward from the baseline, because the baseline
/// carries only the fields worth diffing from and a row's oldest facts may have been pruned by
/// retention. Backwards, the newest version is always complete and the oldest is as complete as
/// the facts allow.
/// </para>
/// </remarks>
public static class VRChatUserHistory
{
    /// <summary>How many versions are replayed. A profile that changed more often than this shows its newest changes.</summary>
    public const int MaxVersions = 200;

    public static RouteGroupBuilder MapVRChatUserHistory(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/history", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                return Results.Ok(await HistoryAsync(id, db, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetVRChatUserHistory")
            .WithSummary("This person's profile as it stood after each recorded change, newest first")
            .WithDescription(
                "Replayed from the profile facts: the newest version is the profile as stored now, "
                + "and each earlier one has that change's old values put back. `factId` is the audit "
                + "log entry that recorded the change. The oldest version is marked `baseline` when it "
                + "is the first sighting Modbot recorded.")
            .Produces<ProfileHistory>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/raw", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                var row = await db.VRChatUsers.AsNoTracking()
                    .Where(u => u.UserId == id)
                    .Select(u => new { u.RawPublicProfile, u.LastRefreshedAt, u.RawProfile, u.LastUserReadAt })
                    .FirstOrDefaultAsync(ct);

                return Results.Ok(new RawProfile(
                    id,
                    AuditJson.Parse(row?.RawPublicProfile),
                    row?.LastRefreshedAt,
                    AuditJson.Parse(row?.RawProfile),
                    row?.LastUserReadAt));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetVRChatUserRaw")
            .WithSummary("The bodies VRChat last sent for this person, as stored")
            .WithDescription(
                "The public profile and the full user object, each as VRChat returned it on the last "
                + "successful read. The user object is stored without instance locations, the "
                + "account's private note and the friend key, which Modbot never keeps.")
            .Produces<RawProfile>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return group;
    }

    internal static async Task<ProfileHistory> HistoryAsync(
        string id, ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.VRChatUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == id, ct);

        if (row is null)
            return new ProfileHistory(id, false, [], now);

        string[] types = [FactType.UserProfileFirstSeen, FactType.UserProfileChanged];

        var facts = await db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.VRChat && e.SubjectId == id && types.Contains(e.Type))
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(MaxVersions)
            .Select(e => new { e.Id, e.Type, e.OccurredAt, e.OccurredBefore, e.Source, e.Data })
            .ToListAsync(ct);

        var state = new State(row);
        var versions = new List<ProfileVersion>(facts.Count);

        foreach (var fact in facts)
        {
            var data = AuditJson.Parse(fact.Data) as JsonObject;

            if (fact.Type == FactType.UserProfileFirstSeen)
            {
                // The record begins here. What the baseline carried is what the walk should
                // already have arrived at; where a diff was lost, the baseline is the truth.
                if (data?["baseline"] is JsonObject baseline)
                {
                    foreach (var (field, value) in baseline)
                        state.Set(field, value);
                }

                versions.Add(new ProfileVersion(
                    fact.Id, fact.OccurredAt, fact.OccurredBefore, fact.Source.ToString(),
                    [], Baseline: true, Current: versions.Count == 0, state.Snapshot()));
                continue;
            }

            var changed = data?["changed"] as JsonObject;
            var fields = changed?.Select(pair => pair.Key).ToList() ?? [];

            versions.Add(new ProfileVersion(
                fact.Id, fact.OccurredAt, fact.OccurredBefore, fact.Source.ToString(),
                fields, Baseline: false, Current: versions.Count == 0, state.Snapshot()));

            // Then the profile as it stood before this change: every old value put back.
            if (changed is not null)
            {
                foreach (var (field, pair) in changed)
                    state.Set(field, (pair as JsonObject)?["old"]);
            }
        }

        return new ProfileHistory(id, true, versions, now);
    }

    /// <summary>The fields a diff can name, under VRChat's own names, and how each is put back.</summary>
    private sealed class State
    {
        private string? _displayName, _bio, _statusDescription, _pronouns;
        private string? _avatarImageUrl, _avatarThumbnailUrl, _profilePictureUrl;
        private string? _dateJoined, _ageVerificationStatus;
        private bool? _ageVerified;
        private List<string> _tags;

        public State(VRChatUser row)
        {
            _displayName = row.DisplayName;
            _bio = row.Bio;
            _statusDescription = row.StatusDescription;
            _pronouns = row.Pronouns;
            _avatarImageUrl = row.CurrentAvatarImageUrl;
            _avatarThumbnailUrl = row.CurrentAvatarThumbnailImageUrl;
            _profilePictureUrl = row.ProfilePictureUrl;
            _dateJoined = row.DateJoined?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _ageVerificationStatus = row.AgeVerificationStatus;
            _ageVerified = row.AgeVerified;
            _tags = Tags(row.Tags);
        }

        public void Set(string field, JsonNode? value)
        {
            switch (field)
            {
                case "displayName": _displayName = Text(value); break;
                case "bio": _bio = Text(value); break;
                case "statusDescription": _statusDescription = Text(value); break;
                case "pronouns": _pronouns = Text(value); break;
                case "currentAvatarImageUrl": _avatarImageUrl = Text(value); break;
                case "currentAvatarThumbnailImageUrl": _avatarThumbnailUrl = Text(value); break;
                case "profilePicOverride": _profilePictureUrl = Text(value); break;
                case "dateJoined": _dateJoined = Day(Text(value)); break;
                case "ageVerificationStatus": _ageVerificationStatus = Text(value); break;
                case "ageVerified": _ageVerified = value is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null; break;
                case "tags":
                    _tags = value is JsonArray array
                        ? array.Select(t => Text(t)).Where(t => t is not null).Select(t => t!).ToList()
                        : [];
                    break;
            }
        }

        public ProfileFields Snapshot() => new(
            _displayName, _bio, _statusDescription, _pronouns,
            _avatarImageUrl, _avatarThumbnailUrl, _profilePictureUrl,
            _dateJoined, [.. _tags], _ageVerificationStatus, _ageVerified);

        private static string? Text(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

        /// <summary>A date written as a full timestamp is shown as its day; anything else is left as sent.</summary>
        private static string? Day(string? text) =>
            text is not null && text.Length >= 10 && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                ? text[..10]
                : text;

        private static List<string> Tags(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return [];

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
