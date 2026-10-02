using System.Globalization;
using System.Text;
using System.Text.Json;
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

namespace Modbot.Api.Features.Lists;

/// <summary>
/// Saved lists: a name and a rule tree, who is in each right now, and taking a copy of them as a
/// file (lists design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A list never does anything by itself</strong> (M7 §7). Everything here either stores
/// what a person decided or answers a question they asked. The one thing that sends people's
/// data anywhere is the export, which a person asks for, and which is recorded as a fact naming
/// them, the list, the format, how many people and which columns.
/// </para>
/// <para>
/// Who is in a list is worked out by the giveaway rule checker every time it is asked, with no
/// exclusions and no weighting, so a list and a giveaway with the same rules name the same people
/// (giveaways design §2.6).
/// </para>
/// <para>
/// <strong>Seeing a list needs See members and See profiles both</strong> (<see cref="ToSee"/>,
/// lists design §6). A list's rules can ask about bans, flags and 18+ verification, so who is in
/// "banned twice" is moderation history and profile data, not only membership.
/// </para>
/// </remarks>
public static class ListEndpoints
{
    /// <summary>What seeing a list, who is in it, or an export of it needs.</summary>
    public const ModbotPermissions ToSee = ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile;

    /// <summary>
    /// What setting up a Discord role from a list needs: the Discord settings tab it lives on, and
    /// the role pairs' own permission (roles from lists design §9).
    /// </summary>
    public const ModbotPermissions ToGiveRoles = ModbotPermissions.ManageSettings | ModbotPermissions.ManageDiscordSync;

    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapLists(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/lists").WithTags("Lists");

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var lists = await db.SavedLists.AsNoTracking()
                    .Where(l => l.DeletedAt == null)
                    .OrderBy(l => l.Name)
                    .ToListAsync(ct);

                var held = ModbotAuth.PermissionsOf(http.User);

                return Results.Ok(new ListsView(
                    await ViewsAsync(db, lists, ct),
                    ModbotAuth.Allows(held, ModbotPermissions.ManageLists),
                    ModbotAuth.Allows(held, ToGiveRoles)));
            })
            .RequiresFlag(ToSee)
            .WithName("ListLists")
            .WithSummary("List saved lists")
            .WithDescription("Every saved list by name, with its rules and what uses it.")
            .Produces<ListsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/builder", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);
                var guildId = settings?.DiscordGuildId;

                var groupRoles = (snapshot?.Roles ?? [])
                    .Select(r => new ListRoleView(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name!))
                    .ToList();

                var discordRoles = guildId is null
                    ? []
                    : await db.DiscordRoles.AsNoTracking()
                        .Where(r => r.GuildId == guildId && r.RemovedAt == null && !r.Everyone)
                        .OrderByDescending(r => r.Position)
                        .Select(r => new ListRoleView(r.RoleId, r.Name))
                        .ToListAsync(ct);

                return Results.Ok(new ListBuilderView(
                    [.. GiveawayRuleKinds.Asking.Where(k => k != GiveawayRuleKinds.InList)],
                    [],
                    [.. TrustRanks.LadderRanks.Select(r => r.ToString())],
                    groupRoles,
                    discordRoles,
                    settings?.ModerationFactRetentionDays ?? 0,
                    settings?.PresenceFactRetentionDays ?? 0));
            })
            .RequiresFlag(ModbotPermissions.ManageLists)
            .WithName("GetListBuilder")
            .WithSummary("Get list builder")
            .WithDescription("The rule kinds and the roles a list's rules can name.")
            .Produces<ListBuilderView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/preview", async (
                [FromBody] ListPreviewRequest body,
                [FromQuery] int? pageSize,
                [FromServices] GiveawayRuleChecker checker,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ReadRules(body.Rules, out var problem) is not { } rules)
                    return Results.BadRequest(new { error = problem });

                var people = await checker.PeopleAsync(rules, ct);
                return Results.Ok(Page(people, 1, Size(pageSize), clock.UtcNow));
            })
            // The builder's, so Manage lists; and it shows people the way a saved list does, so
            // whatever seeing a list needs as well. A preview must not be a way round either.
            .RequiresFlag(ModbotPermissions.ManageLists | ToSee)
            .WithName("PreviewList")
            .WithSummary("Preview list rules")
            .WithDescription(
                "Who a rule tree lets through right now, and the first page of them. Figures counted "
                + "from presence reports are close rather than exact; `fromPolledData` says when that "
                + "is so, and `closeCalls` counts the people near a threshold. A rule reaching further "
                + "back than the facts Modbot still keeps comes back in `unanswerable` instead of an answer.")
            .Produces<ListPeopleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}/people", async (
                [FromRoute] Guid id,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromServices] ModbotContext db,
                [FromServices] GiveawayRuleChecker checker,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var list = await db.SavedLists.AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);

                if (list is null)
                    return Results.NotFound();

                var people = await checker.PeopleAsync(GiveawayRules.ReadStored(list.Rules), ct);
                return Results.Ok(Page(people, Math.Max(page ?? 1, 1), Size(pageSize), clock.UtcNow));
            })
            .RequiresFlag(ToSee)
            .WithName("ListListPeople")
            .WithSummary("List the people in a list")
            .WithDescription(
                "Who is in a saved list right now, by name, a page at a time. Worked out again on "
                + "every request: a list is its rules, not a copy of who matched them once.")
            .Produces<ListPeopleView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] ListRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (await CheckAsync(db, body, except: null, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var now = clock.UtcNow;
                var list = new SavedList
                {
                    Id = Guid.CreateVersion7(),
                    Name = body.Name.Trim(),
                    Rules = GiveawayRules.Store(ReadRules(body.Rules, out _)!),
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.SavedLists.Add(list);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(FactType.ListCreated, list.Id.ToString(), Actor.Of(http), Describe(list), ct);

                await transaction.CommitAsync(ct);

                var views = await ViewsAsync(db, [list], ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageLists)
            .WithName("CreateList")
            .WithSummary("Add list")
            .WithDescription("Save a list: a name and the rules that decide who is in it.")
            .Produces<ListView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] ListRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var list = await db.SavedLists.FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);
                if (list is null)
                    return Results.NotFound();

                if (await CheckAsync(db, body, except: id, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                // A list a giveaway or auto-invites names decides who they reach. Changing it is
                // changing them, so it takes their permission as well as this one (lists design §6).
                var use = await SavedListRules.UseOfAsync(db, id, ct);

                if (Refusal(http, use) is { } refused)
                    return refused;

                var before = Describe(list);
                var rules = ReadRules(body.Rules, out _)!;

                list.Name = body.Name.Trim();
                list.Rules = GiveawayRules.Store(rules);

                if (!db.ChangeTracker.HasChanges())
                    return Results.Ok((await ViewsAsync(db, [list], ct))[0]);

                list.UpdatedAt = clock.UtcNow;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.ListChanged,
                    list.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["name"] = list.Name, ["before"] = before, ["after"] = Describe(list) },
                    ct);

                await transaction.CommitAsync(ct);

                var views = await ViewsAsync(db, [list], ct);
                return Results.Ok(views[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageLists)
            .WithName("UpdateList")
            .WithSummary("Update list")
            .WithDescription(
                "Change a list's name or rules. A list a giveaway still being run names needs Run "
                + "giveaways as well; one auto-invites names needs Set up auto-invites; one a Discord "
                + "role is given from needs Manage role and ban sync.")
            .Produces<ListView>()
            .Produces(StatusCodes.Status400BadRequest)
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
                var list = await db.SavedLists.FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);
                if (list is null)
                    return Results.NotFound();

                // Refused rather than left to fail later: a giveaway or auto-invites naming a list
                // that is gone lets nobody through, and nobody would find out until it mattered.
                var use = await SavedListRules.UseOfAsync(db, id, ct);

                if (use.Any)
                    return Results.Conflict(new { error = InUse(use) });

                var now = clock.UtcNow;
                list.DeletedAt = now;
                list.UpdatedAt = now;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await facts.RecordAsync(
                    FactType.ListDeleted, list.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = list.Name }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageLists)
            .WithName("DeleteList")
            .WithSummary("Delete list")
            .WithDescription(
                "Delete a list. Refused while a giveaway still being run, auto-invites, an event still "
                + "being run, or a Discord role given from the list names it.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/export", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] ListExportRequest body,
                [FromServices] ModbotContext db,
                [FromServices] GiveawayRuleChecker checker,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var format = (body.Format ?? string.Empty).Trim().ToLowerInvariant();
                if (format is not (ExportFormats.Csv or ExportFormats.Json))
                    return Results.BadRequest(new { error = "Export as csv or json." });

                var list = await db.SavedLists.AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);

                if (list is null)
                    return Results.NotFound();

                var people = await checker.PeopleAsync(GiveawayRules.ReadStored(list.Rules), ct);

                if (people.Unanswerable is { } why)
                    return Results.Conflict(new { error = why });

                var columns = ExportColumns.All;
                var now = clock.UtcNow;

                // Recorded before the file is handed over and in the same request, so there is no
                // export that left without a record of who took it (lists design §7).
                await facts.RecordAsync(
                    FactType.ListExported,
                    list.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["name"] = list.Name,
                        ["format"] = format,
                        ["count"] = people.People.Count,
                        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)JsonValue.Create(c))]),
                    },
                    ct);

                var file = $"{FileName(list.Name)}-{now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{format}";

                return format == ExportFormats.Csv
                    ? Results.File(Encoding.UTF8.GetBytes(Csv(people, columns)), "text/csv; charset=utf-8", file)
                    : Results.File(Encoding.UTF8.GetBytes(Json(list, people, columns, now)), "application/json; charset=utf-8", file);
            })
            .RequiresFlag(ToSee)
            .WithName("ExportList")
            .WithSummary("Export list")
            .WithDescription(
                "Everybody in a list right now, as a CSV or JSON file. The file leaves Modbot: once "
                + "downloaded, retention and purge no longer reach it. Every export is recorded with "
                + "who asked, the format, how many people and which columns.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    // ── Reading and checking ─────────────────────────────────────────────────────────────

    /// <summary>A list's rules as submitted, refusing a list inside a list.</summary>
    internal static GiveawayRule? ReadRules(JsonNode? node, out string? problem)
    {
        var rules = GiveawayRules.Read(node, out problem);

        if (problem is not null || rules is null)
        {
            problem ??= "Those rules cannot be read.";
            return null;
        }

        // One pass is then all expanding a tree ever takes, and no list can name itself however
        // indirectly (lists design §4.1).
        if (GiveawayRules.ListsIn(rules).Count > 0)
        {
            problem = "A list cannot use another list.";
            return null;
        }

        return rules;
    }

    /// <summary>What is wrong with a list as submitted, or null when nothing is.</summary>
    private static async Task<string?> CheckAsync(ModbotContext db, ListRequest body, Guid? except, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
            return "A list needs a name.";

        if (name.Length > SavedList.MaxNameLength)
            return $"The name is longer than {SavedList.MaxNameLength} characters.";

        if (ReadRules(body.Rules, out var problem) is null)
            return problem;

        // Two lists with one name would read the same in a giveaway's picker and on its page.
        var lower = name.ToLowerInvariant();
        var taken = await db.SavedLists.AsNoTracking()
            .AnyAsync(l => l.DeletedAt == null && l.Id != except && l.Name.ToLower() == lower, ct);

        return taken ? "There is already a list with that name." : null;
    }

    /// <summary>Refuses a change to a list somebody without its users' permissions may not make.</summary>
    private static IResult? Refusal(HttpContext http, SavedListUse use)
    {
        var held = ModbotAuth.PermissionsOf(http.User);

        if (use.Giveaways.Count > 0 && !ModbotAuth.Allows(held, ModbotPermissions.RunGiveaways))
        {
            return Results.Json(
                new { error = $"The giveaway “{use.Giveaways[0]}” uses this list. Changing it needs Run giveaways." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (use.AutoInvites && !ModbotAuth.Allows(held, ModbotPermissions.ManageAutoInvites))
        {
            return Results.Json(
                new { error = "Auto-invites use this list. Changing it needs Set up auto-invites." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Changing a list a Discord role is given from changes who holds the role (roles from lists
        // design §9), so it takes the role pairs' permission.
        if (use.DiscordRoles.Count > 0 && !ModbotAuth.Allows(held, ModbotPermissions.ManageDiscordSync))
        {
            return Results.Json(
                new { error = $"The Discord role “{use.DiscordRoles[0]}” is given from this list. Changing it needs Manage role and ban sync." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static string InUse(SavedListUse use)
    {
        if (use.Giveaways.Count > 0)
        {
            return use.Giveaways.Count == 1
                ? $"The giveaway “{use.Giveaways[0]}” uses this list."
                : $"The giveaways “{use.Giveaways[0]}” and {use.Giveaways.Count - 1} more use this list.";
        }

        if (use.AutoInvites)
            return "Auto-invites use this list.";

        if (use.DiscordRoles.Count > 0)
        {
            return use.DiscordRoles.Count == 1
                ? $"The Discord role “{use.DiscordRoles[0]}” is given from this list."
                : $"The Discord roles “{use.DiscordRoles[0]}” and {use.DiscordRoles.Count - 1} more are given from this list.";
        }

        return use.Events.Count == 1
            ? $"The event “{use.Events[0]}” invites this list."
            : $"The events “{use.Events[0]}” and {use.Events.Count - 1} more invite this list.";
    }

    private static int Size(int? pageSize) => Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

    // ── Views ────────────────────────────────────────────────────────────────────────────

    private static ListPeopleView Page(GiveawayPeople people, int page, int size, DateTimeOffset now)
    {
        var shown = people.People
            .Skip((page - 1) * size)
            .Take(size)
            .Select(p => new ListPersonView(
                p.Person.Key,
                p.Person.VRChatUserId,
                p.Person.DiscordUserId,
                p.Person.Name,
                p.Person.InGroup,
                p.Person.InDiscord,
                p.Person.Linked,
                p.FromPolledData,
                p.CloseCall))
            .ToList();

        return new ListPeopleView(
            people.People.Count,
            people.Considered,
            people.CloseCalls,
            people.FromPolledData,
            people.Unanswerable,
            people.Stopped,
            now,
            shown,
            page,
            size);
    }

    private static async Task<List<ListView>> ViewsAsync(
        ModbotContext db, IReadOnlyList<SavedList> lists, CancellationToken ct)
    {
        var makers = lists.Where(l => l.CreatedByUserId != null).Select(l => l.CreatedByUserId!.Value).Distinct().ToList();

        var names = await db.Users.AsNoTracking()
            .Where(u => makers.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var uses = await SavedListRules.UsesAsync(db, ct);
        var roleNames = await RoleNamesAsync(db, ct);

        return [.. lists.Select(list =>
        {
            var rules = GiveawayRules.ReadStored(list.Rules);
            var use = uses.GetValueOrDefault(list.Id.ToString("D")) ?? SavedListRules.Unused;

            return new ListView(
                list.Id,
                list.Name,
                GiveawayRules.Write(rules),
                GiveawayRules.DescribeLines(rules, roleNames),
                list.CreatedByUserId is { } by && names.TryGetValue(by, out var username) ? username : null,
                list.CreatedAt,
                list.UpdatedAt,
                new ListUseView(use.Giveaways, use.AutoInvites, use.Events, use.DiscordRoles));
        })];
    }

    /// <summary>The group's and the server's role names by id, so a rule line names the role.</summary>
    private static async Task<Dictionary<string, string>> RoleNamesAsync(ModbotContext db, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var role in GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot)?.Roles ?? [])
        {
            if (!string.IsNullOrWhiteSpace(role.Name))
                names[role.Id] = role.Name!;
        }

        if (settings?.DiscordGuildId is { } guildId)
        {
            var discord = await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == guildId && r.RemovedAt == null)
                .Select(r => new { r.RoleId, r.Name })
                .ToListAsync(ct);

            foreach (var role in discord)
                names[role.RoleId] = role.Name;
        }

        return names;
    }

    private static JsonObject Describe(SavedList list) => new()
    {
        ["name"] = list.Name,
        ["rules"] = JsonNode.Parse(list.Rules),
        ["ruleLines"] = new JsonArray([.. GiveawayRules.DescribeLines(GiveawayRules.ReadStored(list.Rules))
            .Select(l => (JsonNode?)JsonValue.Create(l))]),
    };

    // ── The file ─────────────────────────────────────────────────────────────────────────

    private static class ExportFormats
    {
        public const string Csv = "csv";
        public const string Json = "json";
    }

    /// <summary>
    /// The columns an export carries (lists design §7): who they are, whether and since when they
    /// are in the group and the server, and the profile fields -- trust rank, 18+ verified, the
    /// account's age, when Modbot first saw them. Every one of them is something See members and
    /// See profiles already show, which an export needs.
    /// </summary>
    internal static class ExportColumns
    {
        public static readonly IReadOnlyList<string> All =
        [
            "name", "vrchatUserId", "discordUserId", "linked",
            "inGroup", "joinedGroupAt", "inDiscord", "joinedDiscordAt",
            "trustRank", "is18PlusVerified", "vrchatAccountCreated", "firstSeenAt",
        ];
    }

    private static string? Cell(GiveawayCandidate p, string column) => column switch
    {
        "name" => p.Name,
        "vrchatUserId" => p.VRChatUserId,
        "discordUserId" => p.DiscordUserId,
        "linked" => Bool(p.Linked),
        "inGroup" => Bool(p.InGroup),
        "joinedGroupAt" => p.InGroup ? Instant(p.GroupJoinedAt) : null,
        "inDiscord" => Bool(p.InDiscord),
        "joinedDiscordAt" => p.InDiscord ? Instant(p.DiscordJoinedAt) : null,
        "trustRank" => p.TrustRank?.ToString(),
        "is18PlusVerified" => Bool(p.Is18PlusVerified),
        "vrchatAccountCreated" => p.VRChatJoined?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "firstSeenAt" => Instant(p.FirstSeenAt),
        _ => null,
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? Instant(DateTimeOffset? at)
        => at?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The people as CSV, one row each, with a header.
    /// </summary>
    /// <remarks>
    /// Every name is somebody's own choice, and a spreadsheet runs a cell that starts with
    /// <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> as a formula. Such a cell is written with a
    /// leading apostrophe, which a spreadsheet shows as text and does not run: the file is the
    /// group's people, not a way into the computer of whoever opens it.
    /// </remarks>
    internal static string Csv(GiveawayPeople people, IReadOnlyList<string> columns)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', columns)).Append("\r\n");

        foreach (var person in people.People)
        {
            text.Append(string.Join(',', columns.Select(c => CsvCell(Cell(person.Person, c)))));
            text.Append("\r\n");
        }

        return text.ToString();
    }

    internal static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string Json(SavedList list, GiveawayPeople people, IReadOnlyList<string> columns, DateTimeOffset now)
    {
        var rows = new JsonArray();

        foreach (var person in people.People)
        {
            var row = new JsonObject();
            foreach (var column in columns)
            {
                var value = Cell(person.Person, column);
                row[column] = column is "linked" or "inGroup" or "inDiscord" or "is18PlusVerified"
                    ? value == "true"
                    : value;
            }

            rows.Add(row);
        }

        var file = new JsonObject
        {
            ["list"] = list.Name,
            ["exportedAt"] = now,
            ["rules"] = new JsonArray([.. GiveawayRules.DescribeLines(GiveawayRules.ReadStored(list.Rules))
                .Select(l => (JsonNode?)JsonValue.Create(l))]),
            ["count"] = people.People.Count,
            ["people"] = rows,
        };

        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A list's name as a file name: letters, digits and dashes.</summary>
    private static string FileName(string name)
    {
        var text = new StringBuilder();

        foreach (var c in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
                text.Append(char.ToLowerInvariant(c));
            else if (text.Length > 0 && text[^1] != '-')
                text.Append('-');
        }

        var slug = text.ToString().Trim('-');
        return slug.Length == 0 ? "list" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}
