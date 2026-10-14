using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordInvites;

/// <summary>One invite link, as the Invites tab lists it.</summary>
/// <param name="Name">What staff call it, or null while it has no name.</param>
/// <param name="Source">The source its joins are counted under: its name, "Vanity link", or its code.</param>
/// <param name="Gone">Deleted, expired or used up.</param>
/// <param name="MadeByModbot">Modbot made it from a list of names.</param>
/// <param name="Joins">Everyone who has joined through it, since Modbot started counting.</param>
public sealed record DiscordInviteView(
    Guid Id,
    string Code,
    string? Name,
    string Source,
    bool IsVanity,
    bool MadeByModbot,
    bool Gone,
    int Joins);

/// <summary>One place people joined from, with every invite of the same name counted together.</summary>
/// <param name="Unknown">Joins Modbot could not trace to an invite.</param>
public sealed record DiscordInviteSource(string Name, bool Unknown, int Invites, int JoinsInRange, int JoinsEver);

/// <summary>Joins per day for one source in the chart. The last series, when there are more sources than colours, is everything else.</summary>
public sealed record DiscordInviteSeries(string Name, IReadOnlyList<DayValue> Days);

/// <param name="From">The first day of the range, a UTC day.</param>
/// <param name="To">The last day of the range, today.</param>
/// <param name="ReadError">
/// Why Discord's invites could not be read just now, for somebody who may change settings; null when
/// they were read, and always null for anybody else, since only the first is asked to fix it.
/// </param>
/// <param name="MissingPermission">The read was refused for want of Manage Server.</param>
public sealed record DiscordInvitesResponse(
    DateOnly From,
    DateOnly To,
    int Joins,
    IReadOnlyList<DiscordInviteSource> Sources,
    IReadOnlyList<DiscordInviteSeries> Series,
    IReadOnlyList<DiscordInviteView> Invites,
    string? ReadError,
    bool MissingPermission,
    int MostNameLength,
    int MostCreatedAtOnce);

public sealed record DiscordInviteNameBody(string? Name);

/// <param name="ChannelId">The channel the invites lead to.</param>
/// <param name="Names">One invite is made for each.</param>
public sealed record DiscordInviteCreateBody(string ChannelId, IReadOnlyList<string>? Names);

public sealed record DiscordInviteMadeView(string Name, string? Code, string? Error);

/// <param name="Error">Why the rest were not made, when Discord refused them all.</param>
public sealed record DiscordInviteCreateResponse(IReadOnlyList<DiscordInviteMadeView> Made, string? Error, bool MissingPermission);

/// <summary>
/// Invite tracking: which invite each new Discord member used, the names staff give the invites, and
/// invites Modbot makes from a list of names.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Seeing is analytics, changing is settings.</strong> The page and its charts are read under
/// <see cref="ModbotPermissions.ViewAnalytics"/>, like the rest of the Discord page; naming an invite
/// and making invites need <see cref="ModbotPermissions.ManageSettings"/>.
/// </para>
/// <para>
/// <strong>What Discord needs.</strong> Reading invites needs Manage Server on the bot, making one needs
/// Create Invite in the channel. Only somebody who may fix that is told Discord refused: the read is
/// asked of Discord only for them.
/// </para>
/// </remarks>
public static class DiscordInviteEndpoints
{
    /// <summary>The colours a chart has: the busiest four sources and everything else.</summary>
    private const int ChartSources = 4;

    public const string Vanity = "Vanity link";
    public const string UnknownSource = "Unknown";

    public static IEndpointRouteBuilder MapDiscordInvites(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/invites").WithTags("Discord");

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordInviteActions discord,
                CancellationToken ct,
                [FromQuery] int? days = null) =>
            {
                var span = Math.Clamp(days ?? 30, 1, DiscordInviteLimits.MostDays);

                // Read first, so a new invite shows with its name field on the same visit.
                var read = ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageSettings)
                    ? await discord.ReadAsync(ct)
                    : InviteSiteOutcome.Ok;

                var response = await ReportAsync(db, clock, span, ct);

                return Results.Ok(response with
                {
                    ReadError = read.Done ? null : read.Error,
                    MissingPermission = read.MissingPermission,
                });
            })
            .WithName("ListDiscordInvites")
            .WithSummary("List invites and the joins each brought")
            .WithDescription(
                "The server's invites with their names and how many people joined through each, and the "
                + "joins per source, per day, for the last `days` days (1 to 365, default 30). Invites with "
                + "the same name are one source; the vanity link is its own; joins Modbot could not trace "
                + "are Unknown. For somebody who may change settings it first reads the invites from "
                + "Discord, which needs Manage Server, and says in `readError` when that failed.")
            .Produces<DiscordInvitesResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromBody] DiscordInviteNameBody body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var name = body.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                    name = null;

                if (name is { Length: > DiscordInviteLimits.MostNameLength })
                    return Results.BadRequest(new { error = $"A name is at most {DiscordInviteLimits.MostNameLength} characters." });

                if (name is not null && name.Any(char.IsControl))
                    return Results.BadRequest(new { error = "A name is one line." });

                var invite = await db.DiscordInviteLinks.FirstOrDefaultAsync(i => i.Id == id, ct);
                if (invite is null)
                    return Results.NotFound(new { error = "No such invite." });

                var was = Describe(invite);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                invite.Name = name;
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordInvites")
                    .Items<string>("named", [was], [Describe(invite)])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);
                return Results.NoContent();
            })
            .WithName("NameDiscordInvite")
            .WithSummary("Name an invite")
            .WithDescription(
                "Set what the invite is called, or clear it with a null name. Invites with the same "
                + "name are one source in the joins report. Past joins through it take the new name.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/create", async (
                HttpContext http,
                [FromBody] DiscordInviteCreateBody body,
                [FromServices] IDiscordInviteActions discord,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (string.IsNullOrWhiteSpace(body.ChannelId))
                    return Results.BadRequest(new { error = "Pick the channel the invites lead to." });

                var names = (body.Names ?? [])
                    .Select(n => n?.Trim() ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (names.Count == 0)
                    return Results.BadRequest(new { error = "Give at least one name." });

                if (names.Count > DiscordInviteLimits.MostCreatedAtOnce)
                    return Results.BadRequest(new { error = $"At most {DiscordInviteLimits.MostCreatedAtOnce} invites at a time." });

                if (names.FirstOrDefault(n => n.Length > DiscordInviteLimits.MostNameLength || n.Any(char.IsControl)) is { } bad)
                    return Results.BadRequest(new { error = $"\"{bad}\" is too long or not one line. A name is at most {DiscordInviteLimits.MostNameLength} characters." });

                var outcome = await discord.CreateAsync(body.ChannelId.Trim(), names, ct);

                if (outcome.Site.BotOffline)
                    return Results.Json(new { error = outcome.Site.Error }, statusCode: StatusCodes.Status503ServiceUnavailable);

                var made = outcome.Made.Where(m => m.Code is not null).Select(m => m.Name).ToList();

                if (made.Count > 0)
                {
                    await new SettingsChange("discordInvites")
                        .Items<string>("created", [], made)
                        .RecordAsync(http, ct);
                }

                return Results.Ok(new DiscordInviteCreateResponse(
                    [.. outcome.Made.Select(m => new DiscordInviteMadeView(m.Name, m.Code, m.Error))],
                    outcome.Site.Done ? null : outcome.Site.Error,
                    outcome.Site.MissingPermission));
            })
            .WithName("CreateDiscordInvites")
            .WithSummary("Make an invite for each name")
            .WithDescription(
                "Make one invite for each name, to the given channel, with no expiry and no limit on uses, "
                + "and name each. At most 20 at a time. Needs Create Invite in the channel. Answers 200 with "
                + "what each name came to; a name Discord refused has an `error`, and `missingPermission` "
                + "is set when the permission is why.")
            .Produces<DiscordInviteCreateResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    /// <summary>The source an invite's joins are counted under.</summary>
    public static string SourceOf(string? name, bool isVanity, string code)
        => !string.IsNullOrWhiteSpace(name) ? name.Trim() : isVanity ? Vanity : code;

    private sealed record JoinRow(Guid? InviteId, DateTimeOffset JoinedAt);

    private static string Describe(DiscordInviteLink invite) => $"{invite.Code}: {invite.Name ?? "no name"}";

    private static async Task<DiscordInvitesResponse> ReportAsync(
        ModbotContext db, IModbotClock clock, int span, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var from = today.AddDays(1 - span);
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var invites = await db.DiscordInviteLinks.AsNoTracking()
            .OrderBy(i => i.FirstSeenAt)
            .ToListAsync(ct);

        var everJoins = await db.DiscordInviteJoins.AsNoTracking()
            .GroupBy(j => j.InviteId)
            .Select(g => new { InviteId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var joins = await db.DiscordInviteJoins.AsNoTracking()
            .Where(j => j.JoinedAt >= start)
            .Select(j => new JoinRow(j.InviteId, j.JoinedAt))
            .ToListAsync(ct);

        var byId = invites.ToDictionary(i => i.Id);
        var counts = everJoins.ToDictionary(c => c.InviteId ?? Guid.Empty, c => c.Count);

        string SourceFor(Guid? inviteId)
            => inviteId is { } id && byId.TryGetValue(id, out var invite)
                ? SourceOf(invite.Name, invite.IsVanity, invite.Code)
                : UnknownSource;

        // One entry per source: the invites with a name share one, however many of them there are.
        var sources = new Dictionary<string, (int Invites, int Ever, int InRange)>(StringComparer.OrdinalIgnoreCase);

        foreach (var invite in invites)
        {
            var key = SourceOf(invite.Name, invite.IsVanity, invite.Code);
            var (n, ever, inRange) = sources.GetValueOrDefault(key);
            sources[key] = (n + 1, ever + counts.GetValueOrDefault(invite.Id), inRange);
        }

        var unknownEver = counts.GetValueOrDefault(Guid.Empty);
        if (unknownEver > 0)
            sources[UnknownSource] = (0, unknownEver, 0);

        var inRangeBySource = joins.GroupBy(j => SourceFor(j.InviteId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, list) in inRangeBySource)
        {
            var (n, ever, _) = sources.GetValueOrDefault(key);
            sources[key] = (n, ever, list.Count);
        }

        var table = sources
            .Select(s => new DiscordInviteSource(s.Key, s.Key == UnknownSource, s.Value.Invites, s.Value.InRange, s.Value.Ever))
            .OrderByDescending(s => s.JoinsInRange)
            .ThenByDescending(s => s.JoinsEver)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The chart has five colours: the busiest four sources, then the rest together.
        var busiest = table.Where(s => s.JoinsInRange > 0).Take(ChartSources).Select(s => s.Name).ToList();
        var series = new List<DiscordInviteSeries>();

        foreach (var name in busiest)
            series.Add(new DiscordInviteSeries(name, Days(inRangeBySource[name], from, span)));

        var rest = inRangeBySource
            .Where(p => !busiest.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(p => p.Value)
            .ToList();

        if (rest.Count > 0)
            series.Add(new DiscordInviteSeries("Everything else", Days(rest, from, span)));

        var views = invites
            .OrderByDescending(i => i.GoneAt is null)
            .ThenByDescending(i => counts.GetValueOrDefault(i.Id))
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .Select(i => new DiscordInviteView(
                i.Id,
                i.Code,
                i.Name,
                SourceOf(i.Name, i.IsVanity, i.Code),
                i.IsVanity,
                i.MadeByModbot,
                i.GoneAt is not null,
                counts.GetValueOrDefault(i.Id)))
            .ToList();

        return new DiscordInvitesResponse(
            from,
            today,
            joins.Count,
            table,
            series,
            views,
            null,
            false,
            DiscordInviteLimits.MostNameLength,
            DiscordInviteLimits.MostCreatedAtOnce);
    }

    /// <summary>A count for every day of the range, nought where nobody joined, so a column is drawn for each.</summary>
    private static List<DayValue> Days(IEnumerable<JoinRow> joins, DateOnly from, int span)
    {
        var perDay = joins
            .Select(j => DateOnly.FromDateTime(j.JoinedAt.UtcDateTime))
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        return [.. Enumerable.Range(0, span).Select(i => from.AddDays(i)).Select(d => new DayValue(d, perDay.GetValueOrDefault(d)))];
    }
}
