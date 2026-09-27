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

namespace Modbot.Api.Features.Now;

/// <summary>How many of one kind of thing happened since the person last looked.</summary>
/// <param name="Kind">Which line it is: <c>bans</c>, <c>kicks</c>, <c>joins</c> and so on.</param>
/// <param name="Types">The fact types counted, so the page can open the audit log at exactly these.</param>
public sealed record NowChange(string Kind, int Count, IReadOnlyList<string> Types);

/// <summary>What the Now page's "since you last looked" says.</summary>
/// <param name="Since">Where the counting starts.</param>
/// <param name="LookedBefore">
/// False the first time somebody opens the page, when there is no earlier look to count from and
/// <paramref name="Since"/> is a day ago instead.
/// </param>
/// <param name="Changes">
/// Every kind with at least one, in a fixed order. Null for somebody who may not read the audit
/// log, because every count here is a count of its entries.
/// </param>
public sealed record NowLook(
    DateTimeOffset Since,
    bool LookedBefore,
    DateTimeOffset Now,
    IReadOnlyList<NowChange>? Changes);

/// <summary>
/// The Now page's own data: when this person last looked at it, and what happened since.
/// </summary>
/// <remarks>
/// <para>
/// Everything else on Now is read from the endpoints the other pages already use (flags, reviews,
/// live, health), so a person sees there exactly what their permissions show them elsewhere. Only
/// "since you last looked" needed something new: a time kept per account.
/// </para>
/// <para>
/// <strong>Two times, not one.</strong> <see cref="ModbotUser.NowLookedAt"/> moves forward every time
/// the page reports it is on screen. <see cref="ModbotUser.NowSince"/> moves only when somebody comes
/// back after <see cref="AwayAfter"/> without it, and is what the counts start from. A single time
/// moved on every visit would make a reload answer "nothing since a second ago".
/// </para>
/// <para>
/// <strong>From Modbot's own records.</strong> Every count is of facts already written; nothing here
/// asks VRChat or Discord.
/// </para>
/// </remarks>
public static class NowEndpoints
{
    /// <summary>How long the page must be off screen before the next look starts a new "since".</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(15);

    /// <summary>What a first look counts, with no earlier look to count from.</summary>
    public static readonly TimeSpan FirstLookWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// The lines "since you last looked" can show, in the order it shows them: what moderators did
    /// first, then what needs deciding, then the comings and goings.
    /// </summary>
    public static readonly IReadOnlyList<(string Kind, string[] Types)> Kinds =
    [
        ("bans", [FactType.MemberBanned, FactType.DiscordMemberBanned]),
        ("unbans", [FactType.MemberUnbanned, FactType.DiscordMemberUnbanned]),
        ("kicks", [FactType.MemberKicked, FactType.GroupInstanceKick, FactType.DiscordMemberKicked]),
        ("warns", [FactType.GroupInstanceWarn]),
        ("caseFiles", [FactType.ReportCreated]),
        ("notes", [FactType.NoteAdded]),
        ("flags", [FactType.AutoModFlag]),
        ("joinRequests", [FactType.JoinRequestCreated]),
        ("joins", [FactType.MemberJoined]),
        ("leaves", [FactType.MemberLeft]),
    ];

    public static IEndpointRouteBuilder MapNow(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/now").WithTags("Now").RequireAuthorization()
            .MapPost("/look", async (
                    HttpContext http,
                    [FromServices] ModbotContext db,
                    [FromServices] IModbotClock clock,
                    CancellationToken ct) =>
                {
                    if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                        return Results.Forbid();

                    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
                    if (user is null)
                        return Results.Forbid();

                    var now = clock.UtcNow;
                    Look(user, now);
                    await db.SaveChangesAsync(ct);

                    var since = user.NowSince ?? now - FirstLookWindow;
                    var held = ModbotAuth.PermissionsOf(http.User);

                    var changes = AuditVisibility.CanSee(held, AuditCategory.Moderation)
                        ? await ChangesAsync(db, since, now, ct)
                        : null;

                    return Results.Ok(new NowLook(since, user.NowSince is not null, now, changes));
                })
            .WithName("LookAtNow")
            .WithSummary("Record a look at Now")
            .WithDescription(
                "Called when the Now page opens and every few minutes while it stays on screen. "
                + "Returns what happened since this account last looked: counts of bans, kicks, "
                + "case files, notes, flags, join requests, joins and leaves, from the audit log "
                + "only. The counting starts from the last look before the current one, and a "
                + "look moves it on only after 15 minutes away, so a reload does not reset it. "
                + "`changes` is null without permission to read the audit log.")
            .Produces<NowLook>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// Moves the two times on for a look at <paramref name="now"/>. Back after
    /// <see cref="AwayAfter"/> or more, the last look becomes where the counting starts.
    /// </summary>
    public static void Look(ModbotUser user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.NowLookedAt is { } last && now - last >= AwayAfter)
            user.NowSince = last;

        // Never backwards: two tabs whose looks arrive out of order must not undo each other.
        if (user.NowLookedAt is null || now > user.NowLookedAt)
            user.NowLookedAt = now;
    }

    /// <summary>One grouped count over the type index, then one line per kind with any.</summary>
    internal static async Task<IReadOnlyList<NowChange>> ChangesAsync(
        ModbotContext db,
        DateTimeOffset since,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var types = Kinds.SelectMany(k => k.Types).ToArray();

        var counts = await db.Events.AsNoTracking()
            .Where(e => e.OccurredAt >= since && e.OccurredAt <= now && types.Contains(e.Type))
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Type, g => g.Count, StringComparer.Ordinal, ct);

        return Kinds
            .Select(k => new NowChange(k.Kind, k.Types.Sum(t => counts.GetValueOrDefault(t)), k.Types))
            .Where(c => c.Count > 0)
            .ToList();
    }
}
