using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Shared.HeadsUps;

namespace Modbot.Api.Features.Companion.HeadsUps;

/// <summary>One standing heads-up, as the companion receives it with the roster.</summary>
/// <param name="Kind"><c>pin</c>, <c>keep_an_eye</c>, <c>message</c> or <c>ask_for_help</c>.</param>
/// <param name="SubjectName">The person's name as the roster showed it when it was placed. User-controlled text.</param>
/// <param name="PlacedBy">The VRChat name of the moderator who placed it, or their username.</param>
/// <param name="Mine">Placed from this device, so its own companion raises no card for it.</param>
public sealed record HeadsUpDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("subjectName")] string? SubjectName,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("place")] string? Place,
    [property: JsonPropertyName("placedBy")] string PlacedBy,
    [property: JsonPropertyName("placedAt")] DateTimeOffset PlacedAt,
    [property: JsonPropertyName("mine")] bool Mine);

/// <param name="InstanceId">The instance the moderator is standing in, which is where it is placed.</param>
/// <param name="WorldId">Its world, when the companion knows it.</param>
/// <param name="Kind"><c>pin</c>, <c>keep_an_eye</c>, <c>message</c> or <c>ask_for_help</c>.</param>
/// <param name="SubjectId">The person, for a Keep an eye or a Message on a person. Ignored for the other two.</param>
/// <param name="SubjectName">Their name as the roster shows it.</param>
/// <param name="Text">The moderator's words. Plain text, at most 140 characters, no links.</param>
/// <param name="Place">For Ask for help: one of the listed places.</param>
public sealed record PlaceHeadsUpRequest(
    [property: JsonPropertyName("instanceId")] string? InstanceId,
    [property: JsonPropertyName("worldId")] string? WorldId,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("subjectName")] string? SubjectName,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("place")] string? Place);

/// <summary>
/// Placing and clearing heads-ups, and which stand in an instance (heads-ups, 2026-10-03).
/// </summary>
/// <remarks>
/// <para><strong>Only the staff in that instance.</strong> A heads-up is placed in the instance
/// the placing device is standing in, cleared only by a device standing there, and read only with
/// that instance's roster. The test is <see cref="DeviceLocations"/>, the same one that decides who
/// hears of a flagged join.</para>
/// <para><strong>It is sent when a moderator presses Place, and only then.</strong> Nothing here
/// is filled in by the companion on its own.</para>
/// <para><strong>It never reaches anything else.</strong> No fact is written, so the audit log, the
/// website and Discord never see one; the row is kept for who placed what, and is read nowhere but
/// here.</para>
/// <para><strong>It is not a command.</strong> A heads-up is words for people to read. Nothing a
/// companion does changes because one arrived, beyond drawing it.</para>
/// </remarks>
public static class HeadsUpsHandler
{
    public const string Invalid = "heads_up_invalid";

    public const string NotInInstance = "not_in_instance";

    public const string NotFound = "heads_up_not_found";

    public const string TooMany = "too_many_heads_ups";

    public static async Task<IResult> PlaceAsync(
        int apiVersion,
        PlaceHeadsUpRequest? request,
        HttpContext context,
        DeviceAuthenticator authenticator,
        DeviceLocations locations,
        HeadsUpSignal signal,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var device = authentication.Device!;

        if (request is null)
            return Refused(Invalid, "Send a heads-up.");

        if (request.InstanceId is not { Length: > 0 and <= 256 } instanceId)
            return Refused(Invalid, "An instanceId is required.");

        if (request.WorldId is { Length: > 256 })
            return Refused(Invalid, "The worldId is too long.");

        var world = request.WorldId is { Length: > 0 } named ? named : null;

        if (HeadsUpRules.Parse(request.Kind) is not { } kind)
            return Refused(Invalid, "Unknown kind.");

        var now = clock.UtcNow;

        // Placed where the moderator is standing, and nowhere else.
        if (!locations.IsIn(device.Id, instanceId, now, world))
            return Refused(NotInInstance, "This device is not in that instance.", StatusCodes.Status403Forbidden);

        var settings = await database.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.ManagedGroupId })
            .FirstOrDefaultAsync(ct);

        if (settings?.ManagedGroupId is not { Length: > 0 } groupId)
            return CompanionApiErrors.NotReady();

        var subjectId = HeadsUpRules.TakesPerson(kind) && request.SubjectId is { Length: > 0 and <= 256 } subject
            ? subject
            : null;

        var subjectName = subjectId is null ? null : Trimmed(HeadsUpRules.Clean(request.SubjectName), HeadsUpRules.MaxNameLength);

        // The name is the person's own, sent along so the panel can say who without a lookup. One
        // that reads as a link is left off rather than refusing the heads-up: a person who names
        // themselves after a website is exactly who a moderator may want to keep an eye on. The
        // panel then shows their id.
        if (subjectName is not null && HeadsUpRules.HasLink(subjectName))
            subjectName = null;
        var text = HeadsUpRules.Clean(request.Text);
        var place = kind is HeadsUpKind.AskForHelp ? HeadsUpRules.Place(request.Place) : null;

        if (HeadsUpRules.Problem(kind, subjectId, text, kind is HeadsUpKind.AskForHelp ? request.Place : null) is { } problem)
            return Refused(Invalid, problem);

        var standing = await Standing(database, instanceId, world).CountAsync(ct);
        if (standing >= HeadsUpRules.MostStanding)
        {
            return Refused(
                TooMany,
                $"{HeadsUpRules.MostStanding} heads-ups already stand here. Clear one first.",
                StatusCodes.Status403Forbidden);
        }

        var placedBy = await NameOfAsync(database, device.IssuedToUserId, ct);

        var row = new HeadsUp
        {
            Id = Guid.CreateVersion7(now),
            GroupId = groupId,
            InstanceId = instanceId,
            WorldId = world,
            Kind = kind,
            SubjectId = subjectId,
            SubjectName = subjectName,
            Text = text,
            Place = place,
            PlacedByUserId = device.IssuedToUserId,
            PlacedByName = placedBy,
            PlacedByDeviceId = device.Id,
            PlacedAt = now,
        };

        database.HeadsUps.Add(row);
        await database.SaveChangesAsync(ct);

        TellOthers(locations, signal, device.Id, instanceId, world, now);

        return Results.Ok(ToDto(row, device.Id));
    }

    public static async Task<IResult> ClearAsync(
        int apiVersion,
        Guid id,
        HttpContext context,
        DeviceAuthenticator authenticator,
        DeviceLocations locations,
        HeadsUpSignal signal,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        var device = authentication.Device!;
        var now = clock.UtcNow;

        var row = await database.HeadsUps.FirstOrDefaultAsync(h => h.Id == id, ct);

        // One answer for "no such heads-up" and "not one in the instance you are in": a device
        // elsewhere has no business learning that it exists.
        if (row is null || !locations.IsIn(device.Id, row.InstanceId, now, row.WorldId))
            return Refused(NotFound, "No such heads-up here.", StatusCodes.Status404NotFound);

        // Cleared twice is cleared: two moderators pressing Clear at once both get what they asked for.
        if (row.ClearedAt is not null)
            return Results.NoContent();

        row.ClearedAt = now;
        row.ClearedByUserId = device.IssuedToUserId;
        row.ClearedByName = await NameOfAsync(database, device.IssuedToUserId, ct);
        row.ClearedBecause = HeadsUpEnd.Cleared;
        await database.SaveChangesAsync(ct);

        TellOthers(locations, signal, device.Id, row.InstanceId, row.WorldId, now);

        return Results.NoContent();
    }

    /// <summary>
    /// What stands in an instance, for its roster read, after ending what is due.
    /// </summary>
    /// <remarks>
    /// <para>Ending is decided here, against the roster this read has just worked out, because that
    /// roster is the one place Modbot believes who is in an instance. When it says the instance has
    /// closed, or nobody is in it, everything there ends. A message on a person ends when that person
    /// is no longer on it. A Keep an eye does not end when its person leaves, so their coming back
    /// can be told; it ends with the instance.</para>
    /// <para>The end is written to the row, so a heads-up does not come back to life if the person
    /// does.</para>
    /// </remarks>
    /// <param name="here">Everybody the roster has in the instance now.</param>
    /// <param name="instanceEnded">The instance has closed, or the roster is empty.</param>
    /// <returns>What stands, oldest first, and whether anything ended just now.</returns>
    internal static async Task<(IReadOnlyList<HeadsUpDto> Standing, bool Ended)> StandingAsync(
        ModbotContext database,
        Guid deviceId,
        string instanceId,
        string? worldId,
        IReadOnlySet<string> here,
        bool instanceEnded,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rows = await Standing(database, instanceId, worldId)
            .OrderBy(h => h.PlacedAt)
            .ToListAsync(ct);

        if (rows.Count == 0)
            return ([], false);

        var ended = false;
        var standing = new List<HeadsUpDto>(rows.Count);

        foreach (var row in rows)
        {
            HeadsUpEnd? end = instanceEnded
                ? HeadsUpEnd.InstanceEnded
                : row.Kind is HeadsUpKind.Message && row.SubjectId is { } subject && !here.Contains(subject)
                    ? HeadsUpEnd.PersonLeft
                    : null;

            if (end is { } because)
            {
                row.ClearedAt = now;
                row.ClearedBecause = because;
                ended = true;
                continue;
            }

            standing.Add(ToDto(row, deviceId));
        }

        if (ended)
            await database.SaveChangesAsync(ct);

        return (standing, ended);
    }

    /// <summary>Tells every other device in the instance to read its heads-ups again.</summary>
    internal static void TellOthers(
        DeviceLocations locations,
        HeadsUpSignal signal,
        Guid deviceId,
        string instanceId,
        string? worldId,
        DateTimeOffset now)
        => signal.Tell(locations.DevicesIn(instanceId, now, worldId).Where(d => d != deviceId));

    /// <summary>
    /// The standing heads-ups at this address. A row placed without a world, or a question that
    /// names none, is matched by the number alone, as the roster is.
    /// </summary>
    private static IQueryable<HeadsUp> Standing(ModbotContext database, string instanceId, string? worldId)
        => database.HeadsUps.Where(h => h.InstanceId == instanceId
            && h.ClearedAt == null
            && (worldId == null || h.WorldId == null || h.WorldId == worldId));

    private static HeadsUpDto ToDto(HeadsUp row, Guid deviceId) => new(
        row.Id.ToString("n"),
        HeadsUpRules.Word(row.Kind),
        row.SubjectId,
        row.SubjectName,
        row.Text,
        row.Place,
        row.PlacedByName,
        row.PlacedAt,
        row.PlacedByDeviceId == deviceId);

    /// <summary>The name moderators know each other by in VRChat, or the username when it is not known.</summary>
    private static async Task<string> NameOfAsync(ModbotContext database, Guid userId, CancellationToken ct)
    {
        var user = await database.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.VRChatDisplayName, u.Username })
            .FirstOrDefaultAsync(ct);

        var name = user?.VRChatDisplayName is { Length: > 0 } vrchat ? vrchat : user?.Username ?? string.Empty;
        return Trimmed(name, 64) ?? string.Empty;
    }

    private static string? Trimmed(string? text, int longest)
        => text is { Length: > 0 } && text.Length > longest ? text[..longest] : text;

    private static IResult Refused(string code, string message, int status = StatusCodes.Status400BadRequest)
        => Results.Json(new CompanionError(code, message), statusCode: status);
}
