using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Giveaways;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Giveaways;

/// <summary>
/// Giveaways: planning them, opening and closing them, drawing them, and showing how each draw
/// went (giveaways design).
/// </summary>
/// <remarks>
/// <para>
/// This stores what a person decides and draws when asked. Posting to Discord happens in the
/// giveaway's own loop, which reads what is saved here — so a request never waits on Discord, and
/// a giveaway edited three times in a minute is one message edit.
/// </para>
/// <para>
/// Every change is a fact, written in the same transaction as the change itself.
/// </para>
/// </remarks>
public static class GiveawayEndpoints
{
    /// <summary>The longest a giveaway may be open. Longer than this and nobody remembers entering.</summary>
    public static readonly TimeSpan MaxOpenFor = TimeSpan.FromDays(180);

    /// <summary>How long after closing a draw may be scheduled.</summary>
    public static readonly TimeSpan MaxDrawAfter = TimeSpan.FromDays(30);

    public const int MaxEntrantPageSize = 200;

    public static IEndpointRouteBuilder MapGiveaways(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/giveaways").WithTags("Giveaways");

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] string? state,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var wanted = state?.Trim();

                if (wanted is { Length: > 0 } && !GiveawayStates.All.Contains(wanted, StringComparer.Ordinal))
                    return Results.BadRequest(new { error = "That is not a giveaway status." });

                var giveaways = await db.Giveaways.AsNoTracking()
                    .Where(g => g.DeletedAt == null && (wanted == null || wanted == "" || g.State == wanted))
                    .OrderByDescending(g => g.ClosesAt)
                    .Take(500)
                    .ToListAsync(ct);

                return Results.Ok(new GiveawayListView(
                    await ViewsAsync(db, giveaways, ct),
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.RunGiveaways),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewGiveaways)
            .WithName("ListGiveaways")
            .WithSummary("List giveaways")
            .WithDescription("Every giveaway, newest first, with its rules, its post and its draws.")
            .Produces<GiveawayListView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var giveaway = await db.Giveaways.AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);

                if (giveaway is null)
                    return Results.NotFound();

                var views = await ViewsAsync(db, [giveaway], ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ViewGiveaways)
            .WithName("GetGiveaway")
            .WithSummary("Get giveaway")
            .WithDescription("One giveaway.")
            .Produces<GiveawayView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/draws/{drawId:guid}/entrants", async (
                [FromRoute] Guid id,
                [FromRoute] Guid drawId,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var draw = await db.GiveawayDraws.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == drawId && d.GiveawayId == id, ct);

                if (draw is null)
                    return Results.NotFound();

                var number = Math.Max(page ?? 1, 1);
                var size = Math.Clamp(pageSize ?? 50, 1, MaxEntrantPageSize);

                var query = db.GiveawayEntrants.AsNoTracking().Where(e => e.DrawId == drawId);
                var total = await query.CountAsync(ct);

                var rows = await query
                    .OrderBy(e => e.Position)
                    .Skip((number - 1) * size)
                    .Take(size)
                    .ToListAsync(ct);

                return Results.Ok(new GiveawayEntrantsView([.. rows.Select(Entrant)], total, number, size));
            })
            .RequiresFlag(ModbotPermissions.ViewGiveaways)
            .WithName("ListGiveawayEntrants")
            .WithSummary("List draw entrants")
            .WithDescription(
                "The list the draw actually ran on, with every weight. With the draw's seed this is "
                + "everything needed to work the result out again.")
            .Produces<GiveawayEntrantsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/builder", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);
                var guildId = settings?.DiscordGuildId;

                var groupRoles = (snapshot?.Roles ?? [])
                    .Select(r => new GiveawayRoleView(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name!))
                    .ToList();

                var discordRoles = guildId is null
                    ? []
                    : await db.DiscordRoles.AsNoTracking()
                        .Where(r => r.GuildId == guildId && r.RemovedAt == null && !r.Everyone)
                        .OrderByDescending(r => r.Position)
                        .Select(r => new GiveawayRoleView(r.RoleId, r.Name))
                        .ToListAsync(ct);

                return Results.Ok(new GiveawayBuilderView(
                    GiveawayRuleKinds.Asking,
                    GiveawayWeights.All,
                    [.. TrustRanks.LadderRanks.Select(r => r.ToString())],
                    groupRoles,
                    discordRoles,
                    settings?.ModerationFactRetentionDays ?? 0,
                    settings?.PresenceFactRetentionDays ?? 0));
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("GetGiveawayBuilder")
            .WithSummary("Get giveaway builder")
            .WithDescription("The rule kinds, the weightings and the roles a rule can name.")
            .Produces<GiveawayBuilderView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/preview", async (
                [FromBody] GiveawayPreviewRequest body,
                [FromServices] GiveawayRuleChecker checker,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var rules = GiveawayRules.Read(body.Rules, out var ruleProblem);
                if (ruleProblem is not null || rules is null)
                    return Results.BadRequest(new { error = ruleProblem ?? "Those rules cannot be read." });

                var exclusions = GiveawayExclusions.Read(body.Exclusions, out var exclusionProblem);
                if (exclusionProblem is not null)
                    return Results.BadRequest(new { error = exclusionProblem });

                var weighting = string.IsNullOrWhiteSpace(body.Weighting) ? GiveawayWeights.Uniform : body.Weighting.Trim();
                if (!GiveawayWeights.IsKnown(weighting))
                    return Results.BadRequest(new { error = "That is not a way of weighting." });

                if (body.WeightCap is { } cap && (cap < 1 || cap > GiveawayWeight.MaxCap))
                    return Results.BadRequest(new { error = $"A cap must be between 1 and {GiveawayWeight.MaxCap:N0}." });

                // A preview of a react giveaway before anybody has reacted would say nought every
                // time, which tells nobody anything. It shows who would qualify if they did react.
                var match = await checker.PreviewAsync(rules, exclusions, weighting, body.WeightCap, ct: ct);

                return Results.Ok(new GiveawayPreviewView(
                    match.Total,
                    match.InDraw,
                    match.TotalWeight,
                    match.CloseCalls,
                    match.FromPolledData,
                    match.Unanswerable,
                    match.Stopped,
                    [.. match.People.Select(Listing)]));
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("PreviewGiveawayRules")
            .WithSummary("Preview giveaway rules")
            .WithDescription(
                "How many people match a rule tree right now, and the first page of them. "
                + "Figures counted from presence reports are close rather than exact; `fromPolledData` "
                + "says when that is so, and `closeCalls` counts the people near a threshold. A rule "
                + "reaching further back than the facts Modbot still keeps comes back in "
                + "`unanswerable` instead of an answer.")
            .Produces<GiveawayPreviewView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] GiveawayRequest body,
                [FromServices] ModbotContext db,
                [FromServices] GiveawayDrawer drawer,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var now = clock.UtcNow;
                var giveaway = new Giveaway
                {
                    Id = Guid.CreateVersion7(),
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                };

                if (Apply(body, giveaway) is { } problem)
                    return Results.BadRequest(new { error = problem });

                giveaway.State = body.Draft ? GiveawayStates.Draft : GiveawayStates.Open;

                if (!body.Draft)
                    giveaway.OpenedAt = now;

                // The promise is made when the giveaway is, so it is standing before anybody could
                // have seen who would win under it (giveaways design §5).
                drawer.Promise(giveaway);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Giveaways.Add(giveaway);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.GiveawayCreated, giveaway.Id.ToString(), Actor.Of(http), Describe(giveaway), ct);

                await transaction.CommitAsync(ct);

                var views = await ViewsAsync(db, [giveaway], ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("CreateGiveaway")
            .WithSummary("Add giveaway")
            .WithDescription("Plan a giveaway.")
            .Produces<GiveawayView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] GiveawayRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var giveaway = await db.Giveaways.FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);
                if (giveaway is null)
                    return Results.NotFound();

                if (giveaway.State == GiveawayStates.Cancelled)
                    return Results.Conflict(new { error = "A cancelled giveaway cannot be changed." });

                // A drawn giveaway keeps its parameters. The draw copied them, so editing them
                // would not change the result -- but a page showing today's rules beside last
                // week's winners is a page that reads as though the two go together.
                if (giveaway.State == GiveawayStates.Drawn)
                    return Results.Conflict(new { error = "A giveaway that has been drawn cannot be changed." });

                if (body.Draft && giveaway.State != GiveawayStates.Draft)
                    return Results.Conflict(new { error = "An open giveaway cannot go back to being a draft." });

                var before = Describe(giveaway);
                var now = clock.UtcNow;

                if (Apply(body, giveaway) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (!body.Draft && giveaway.State == GiveawayStates.Draft)
                {
                    giveaway.State = GiveawayStates.Open;
                    giveaway.OpenedAt = now;
                }

                giveaway.Version++;
                giveaway.UpdatedAt = now;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.GiveawayChanged,
                    giveaway.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["name"] = giveaway.Name, ["before"] = before, ["after"] = Describe(giveaway) },
                    ct);

                await transaction.CommitAsync(ct);

                var views = await ViewsAsync(db, [giveaway], ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("UpdateGiveaway")
            .WithSummary("Update giveaway")
            .WithDescription("Change a giveaway.")
            .Produces<GiveawayView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/open", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var giveaway = await db.Giveaways.FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);
                if (giveaway is null)
                    return Results.NotFound();

                if (giveaway.State is GiveawayStates.Cancelled or GiveawayStates.Drawn)
                    return Results.Conflict(new { error = "That giveaway is finished." });

                if (giveaway.State == GiveawayStates.Open)
                    return Results.NoContent();

                var now = clock.UtcNow;
                giveaway.State = GiveawayStates.Open;
                giveaway.OpenedAt ??= now;
                giveaway.ClosedAt = null;
                giveaway.UpdatedAt = now;
                giveaway.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.GiveawayOpened, giveaway.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = giveaway.Name }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("OpenGiveaway")
            .WithSummary("Open giveaway")
            .WithDescription("Open a giveaway for entries.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/close", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var giveaway = await db.Giveaways.FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);
                if (giveaway is null)
                    return Results.NotFound();

                if (giveaway.State != GiveawayStates.Open)
                    return Results.Conflict(new { error = "That giveaway is not open." });

                var now = clock.UtcNow;
                giveaway.State = GiveawayStates.Closed;
                giveaway.ClosedAt = now;
                giveaway.UpdatedAt = now;
                giveaway.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.GiveawayClosed, giveaway.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = giveaway.Name }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("CloseGiveaway")
            .WithSummary("Close giveaway")
            .WithDescription("Close a giveaway to entries.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/draw", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] GiveawayDrawer drawer,
                CancellationToken ct) =>
            {
                var giveaway = await db.Giveaways.FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);
                if (giveaway is null)
                    return Results.NotFound();

                var result = await drawer.DrawAsync(giveaway, ModbotAuth.UserIdOf(http.User), ct);

                if (result.Problem is { } problem)
                    return Results.Conflict(new { error = problem });

                var views = await ViewsAsync(db, [giveaway], ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("DrawGiveaway")
            .WithSummary("Draw giveaway")
            .WithDescription(
                "Draw a giveaway. Drawing again makes a new draw beside the old one. "
                + "Freezes the entrant list with every weight, reveals the seed that was promised, "
                + "and picks the winners from the two. The rules are checked again here, so "
                + "somebody who qualified when they entered and does not now is shown as such "
                + "rather than dropped.")
            .Produces<GiveawayView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/cancel", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var giveaway = await db.Giveaways.FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);
                if (giveaway is null)
                    return Results.NotFound();

                if (giveaway.State == GiveawayStates.Cancelled)
                    return Results.NoContent();

                var now = clock.UtcNow;
                giveaway.State = GiveawayStates.Cancelled;
                giveaway.CancelledAt = now;
                giveaway.UpdatedAt = now;
                giveaway.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.GiveawayCancelled, giveaway.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = giveaway.Name }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("CancelGiveaway")
            .WithSummary("Cancel giveaway")
            .WithDescription("Cancel a giveaway. Its Discord post says so and nobody else can enter.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var giveaway = await db.Giveaways.FirstOrDefaultAsync(g => g.Id == id && g.DeletedAt == null, ct);
                if (giveaway is null)
                    return Results.NotFound();

                var now = clock.UtcNow;

                if (GiveawayStates.IsLive(giveaway.State))
                {
                    giveaway.State = GiveawayStates.Cancelled;
                    giveaway.CancelledAt = now;
                }

                giveaway.DeletedAt = now;
                giveaway.UpdatedAt = now;
                giveaway.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.GiveawayDeleted, giveaway.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = giveaway.Name }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.RunGiveaways)
            .WithName("DeleteGiveaway")
            .WithSummary("Delete giveaway")
            .WithDescription(
                "Delete a giveaway. Its Discord post is taken down; its draws stay in the audit log.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>Checks a request and copies it onto the giveaway. Returns what is wrong, or null.</summary>
    public static string? Apply(GiveawayRequest body, Giveaway target)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(target);

        var name = body.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "A giveaway needs a name.";

        if (name.Length > Giveaway.MaxNameLength)
            return $"The name is longer than {Giveaway.MaxNameLength} characters.";

        var prize = body.Prize?.Trim() ?? string.Empty;
        if (prize.Length > Giveaway.MaxPrizeLength)
            return $"The prize is longer than {Giveaway.MaxPrizeLength} characters.";

        if (body.ClosesAt <= body.OpensAt)
            return "It must close after it opens.";

        if (body.ClosesAt - body.OpensAt > MaxOpenFor)
            return "A giveaway can be open for at most 180 days.";

        if (body.DrawAt is { } drawAt)
        {
            if (drawAt < body.ClosesAt)
                return "The draw cannot be before it closes.";

            if (drawAt - body.ClosesAt > MaxDrawAfter)
                return "The draw must be within 30 days of closing.";
        }

        if (body.WinnerCount < 1 || body.WinnerCount > Giveaway.MaxWinners)
            return $"There must be between 1 and {Giveaway.MaxWinners} winners.";

        var entryWay = string.IsNullOrWhiteSpace(body.EntryWay) ? GiveawayEntryWays.Automatic : body.EntryWay.Trim();
        if (!GiveawayEntryWays.All.Contains(entryWay, StringComparer.Ordinal))
            return "People enter either automatically or by reacting.";

        var emoji = string.IsNullOrWhiteSpace(body.Emoji) ? "🎉" : body.Emoji.Trim();
        if (emoji.Length > 64)
            return "That emoji is too long.";

        var channelId = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();

        if (body.PostToChannel && channelId is null)
            return "Pick a channel to post to.";

        // Reacting needs somewhere to react. Without a post there is nothing to react to, and a
        // giveaway nobody can enter is not a giveaway.
        if (entryWay == GiveawayEntryWays.React && (!body.PostToChannel || channelId is null))
            return "A giveaway people enter by reacting has to be posted to a channel.";

        var rules = GiveawayRules.Read(body.Rules, out var ruleProblem);
        if (ruleProblem is not null || rules is null)
            return ruleProblem ?? "Those rules cannot be read.";

        var exclusions = GiveawayExclusions.Read(body.Exclusions, out var exclusionProblem);
        if (exclusionProblem is not null)
            return exclusionProblem;

        var weighting = string.IsNullOrWhiteSpace(body.Weighting) ? GiveawayWeights.Uniform : body.Weighting.Trim();
        if (!GiveawayWeights.IsKnown(weighting))
            return "That is not a way of weighting.";

        if (body.WeightCap is { } cap && (cap < 1 || cap > GiveawayWeight.MaxCap))
            return $"A cap must be between 1 and {GiveawayWeight.MaxCap:N0}.";

        // A cap on a giveaway where everybody weighs the same is a control that does nothing, and
        // a control that does nothing is a question somebody will ask later.
        if (weighting == GiveawayWeights.Uniform && body.WeightCap is not null)
            return "A cap only means something when the weighting is not the same for everybody.";

        target.Name = name;
        target.Prize = prize;
        target.OpensAt = body.OpensAt;
        target.ClosesAt = body.ClosesAt;
        target.DrawAt = body.DrawAt;
        target.WinnerCount = body.WinnerCount;
        target.EntryWay = entryWay;
        target.Emoji = emoji;
        target.Rules = GiveawayRules.Store(rules);
        target.Exclusions = exclusions.Store();
        target.Weighting = weighting;
        target.WeightCap = body.WeightCap;
        target.PostToChannel = body.PostToChannel;
        target.ChannelId = channelId;

        return null;
    }

    internal static async Task<List<GiveawayView>> ViewsAsync(
        ModbotContext db, IReadOnlyList<Giveaway> giveaways, CancellationToken ct)
    {
        var ids = giveaways.Select(g => g.Id).ToList();

        var posts = await db.GiveawayPosts.AsNoTracking()
            .Where(p => ids.Contains(p.GiveawayId))
            .ToListAsync(ct);

        var draws = await db.GiveawayDraws.AsNoTracking()
            .Where(d => ids.Contains(d.GiveawayId))
            .OrderByDescending(d => d.Number)
            .ToListAsync(ct);

        var drawIds = draws.Select(d => d.Id).ToList();

        // Only the winners are carried on the giveaway itself. The rest of the list is a page at
        // a time, because a snapshot can be tens of thousands of rows and nothing on this page
        // wants all of them at once (M7 §5).
        var winners = await db.GiveawayEntrants.AsNoTracking()
            .Where(e => drawIds.Contains(e.DrawId) && e.WinnerRank != null)
            .OrderBy(e => e.WinnerRank)
            .ToListAsync(ct);

        var drawnByIds = draws.Where(d => d.DrawnByUserId != null).Select(d => d.DrawnByUserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => drawnByIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var entryCounts = await db.GiveawayEntries.AsNoTracking()
            .Where(e => ids.Contains(e.GiveawayId) && e.WithdrawnAt == null)
            .GroupBy(e => e.GiveawayId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, ct);

        return [.. giveaways.Select(giveaway =>
        {
            var rules = GiveawayRules.ReadStored(giveaway.Rules);
            var exclusions = GiveawayExclusions.ReadStored(giveaway.Exclusions);
            var post = posts.FirstOrDefault(p => p.GiveawayId == giveaway.Id);

            return new GiveawayView(
                giveaway.Id,
                giveaway.Name,
                giveaway.Prize,
                giveaway.OpensAt,
                giveaway.ClosesAt,
                giveaway.DrawAt,
                giveaway.WinnerCount,
                giveaway.EntryWay,
                giveaway.Emoji,
                GiveawayRules.Write(rules),
                GiveawayRules.DescribeLines(rules),
                exclusions.Write(),
                exclusions.Describe(),
                giveaway.Weighting,
                giveaway.WeightCap,
                giveaway.PostToChannel,
                giveaway.ChannelId,
                giveaway.State,
                giveaway.SeedPromise,
                giveaway.DrawCount,
                entryCounts.GetValueOrDefault(giveaway.Id),
                giveaway.Version,
                giveaway.CreatedAt,
                giveaway.UpdatedAt,
                giveaway.OpenedAt,
                giveaway.ClosedAt,
                post is null
                    ? null
                    : new GiveawayPostView(post.State, post.ChannelId, post.Error, post.ErrorAt, post.UpdatedAt),
                [.. draws
                    .Where(d => d.GiveawayId == giveaway.Id)
                    .Select(d => Draw(d, winners.Where(w => w.DrawId == d.Id).ToList(), names))]);
        })];
    }

    private static GiveawayDrawView Draw(
        GiveawayDraw draw, IReadOnlyList<GiveawayEntrant> winners, IReadOnlyDictionary<Guid, string> names)
    {
        var rules = GiveawayRules.ReadStored(draw.Rules);
        var exclusions = GiveawayExclusions.ReadStored(draw.Exclusions);

        return new GiveawayDrawView(
            draw.Id.ToString(),
            draw.Number,
            draw.DrawnAt,
            draw.DrawnByUserId is { } by && names.TryGetValue(by, out var username) ? username : null,
            draw.Seed,
            draw.SeedPromise,
            GiveawayDraws.Keeps(draw.SeedPromise, draw.Seed),
            draw.WinnerCount,
            GiveawayRules.DescribeLines(rules),
            exclusions.Describe(),
            draw.Weighting,
            draw.WeightCap,
            draw.EntrantCount,
            draw.InDrawCount,
            draw.TotalWeight,
            draw.FromPolledData,
            draw.CloseCalls,
            [.. winners.Select(Entrant)]);
    }

    private static GiveawayEntrantView Entrant(GiveawayEntrant row) => new(
        row.Position,
        row.Key,
        row.VRChatUserId,
        row.DiscordUserId,
        row.Name,
        row.Weight,
        row.Measured,
        row.KeptOut,
        GiveawayKeptOut.Label(row.KeptOut),
        row.Because,
        row.FromPolledData,
        row.CloseCall,
        row.WinnerRank,
        row.Purged);

    private static GiveawayEntrantView Listing(GiveawayListing listing) => new(
        0,
        listing.Key,
        listing.VRChatUserId,
        listing.DiscordUserId,
        listing.Name,
        listing.Weight,
        listing.Measured,
        listing.KeptOut,
        GiveawayKeptOut.Label(listing.KeptOut),
        listing.Because,
        listing.FromPolledData,
        listing.CloseCall,
        null,
        false);

    private static JsonObject Describe(Giveaway g) => new()
    {
        ["name"] = g.Name,
        ["prize"] = g.Prize,
        ["opensAt"] = g.OpensAt,
        ["closesAt"] = g.ClosesAt,
        ["drawAt"] = g.DrawAt,
        ["winnerCount"] = g.WinnerCount,
        ["entryWay"] = g.EntryWay,
        ["emoji"] = g.Emoji,
        ["rules"] = JsonNode.Parse(g.Rules),
        ["exclusions"] = JsonNode.Parse(g.Exclusions),
        ["weighting"] = g.Weighting,
        ["weightCap"] = g.WeightCap,
        ["postToChannel"] = g.PostToChannel,
        ["channelId"] = g.ChannelId,
        ["state"] = g.State,
        ["seedPromise"] = g.SeedPromise,
    };
}
