using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI.Chat;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Members;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>Stored VRChat profiles, by id or by name.</summary>
internal sealed class FindPersonTool : ReadTool
{
    public override string Name => "find_person";

    public override string Label => "Find a person";

    public override string Description =>
        "Find VRChat users Modbot has a profile for, by exact VRChat user id or by part of their "
        + "display name. Returns up to 10 matches with their ids. Use this first when you only "
        + "have a name.";

    protected override string Schema => """
        {"type":"object","properties":{"query":{"type":"string","description":"A VRChat user id or part of a display name."}},"required":["query"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewProfile;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "query") is not { } query)
            return ChatToolResult.Problem("query is required.");

        var db = Get<ModbotContext>(context);
        var pattern = Contains(query);

        var people = await db.VRChatUsers.AsNoTracking()
            .Where(u => u.UserId == query || (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\")))
            .OrderByDescending(u => u.UserId == query)
            .ThenByDescending(u => u.LastSeenAt)
            .Take(10)
            .Select(u => new { u.UserId, u.DisplayName, u.LastSeenAt })
            .ToListAsync(ct);

        return ChatToolResult.Json(new { people }, people.Select(p => Person(p.UserId, p.DisplayName)));
    }
}

/// <summary>One person's stored profile, and their membership when the person asking may see members.</summary>
internal sealed class GetPersonTool : ReadTool
{
    public override string Name => "get_person";

    public override string Label => "Open a profile";

    public override string Description =>
        "What Modbot has stored about one VRChat user: display name, bio, status, pronouns, when "
        + "they joined VRChat, the 18+ flag, when Modbot first and last saw them, and how old the "
        + "profile is. Includes group membership and ban standing when available.";

    protected override string Schema => """
        {"type":"object","properties":{"userId":{"type":"string","description":"The VRChat user id."}},"required":["userId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewProfile;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "userId") is not { } id)
            return ChatToolResult.Problem("userId is required.");

        var db = Get<ModbotContext>(context);
        var clock = Get<IModbotClock>(context);

        var profile = await VRChatUserEndpoints.ProfileAsync(
            id,
            db,
            clock,
            Get<IVRChatGate>(context),
            context.Services.GetService<UserRefreshQueue>(),
            context.Services.GetService<UserProfileSyncOptions>(),
            ct);

        // The same split the popup makes: membership is the Members permission's to show.
        object? membership = null;
        if (ChatToolRegistry.Allows(context.Held, ModbotPermissions.ViewMembers))
        {
            var m = await MemberEndpoints.MembershipAsync(id, db, clock, ct);
            membership = new
            {
                m.Known,
                m.IsMember,
                roles = m.RoleNames,
                m.JoinedAt,
                m.LeftAt,
                m.Banned,
                m.BannedAt,
                m.BanLiftedAt,
            };
        }

        return ChatToolResult.Json(
            new
            {
                profile.UserId,
                profile.Known,
                profile.DisplayName,
                profile.Bio,
                profile.Status,
                profile.StatusDescription,
                profile.Pronouns,
                profile.DateJoined,
                profile.Tags,
                profile.LastPlatform,
                eighteenPlusVerified = profile.EighteenPlus,
                profile.FirstSeenAt,
                profile.LastSeenAt,
                profile.LastRefreshedAt,
                profile.Stale,
                profile.NotFoundAt,
                membership,
            },
            [Person(profile.UserId, profile.DisplayName)]);
    }
}

/// <summary>The audit log for one subject.</summary>
internal sealed class PersonHistoryTool : ReadTool
{
    public override string Name => "get_person_history";

    public override string Label => "Person's history";

    public override string Description =>
        "The most recent audit log entries about one person, newest first: joins, leaves, bans, "
        + "kicks, role changes, warnings and anything else recorded about them.";

    protected override string Schema => """
        {"type":"object","properties":{"userId":{"type":"string","description":"The VRChat user id."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"How many entries. Default 25."}},"required":["userId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAuditLog;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "userId") is not { } id)
            return ChatToolResult.Problem("userId is required.");

        var limit = ChatArguments.Number(arguments, "limit", 25, 1, 50);
        var entries = await AuditSearch.RunAsync(context, [], id, null, null, null, limit, ct);

        return entries;
    }
}

/// <summary>Case files written about one person.</summary>
internal sealed class PersonCasesTool : ReadTool
{
    public override string Name => "get_person_cases";

    public override string Label => "Person's case files";

    public override string Description =>
        "Case files (ban write-ups) about one person, newest first: when they were banned, who "
        + "wrote the case file, the reasons picked, and whether it was withdrawn.";

    protected override string Schema => """
        {"type":"object","properties":{"userId":{"type":"string","description":"The VRChat user id."}},"required":["userId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewProfile;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "userId") is not { } id)
            return ChatToolResult.Problem("userId is required.");

        var list = await new CaseFileService(Get<ModbotContext>(context), Get<IModbotClock>(context))
            .ListAsync(id, includeWithdrawn: true, offset: 0, limit: 20, ct);

        return ChatToolResult.Json(
            new
            {
                list.Total,
                cases = list.Cases.Select(c => new
                {
                    caseId = c.Id,
                    c.UserId,
                    c.DisplayName,
                    c.BannedAt,
                    writtenBy = c.AuthorUsername,
                    reasons = c.Reasons.Select(r => r.Label),
                    c.CreatedAt,
                    c.Withdrawn,
                    c.EvidenceCount,
                }),
            },
            [
                .. list.Cases.Select(c => Person(c.UserId, c.DisplayName)),
                .. list.Cases.Select(c => Case(c.Id, c.DisplayName is { Length: > 0 } name ? $"Case: {name}" : "Case file")),
            ]);
    }
}

/// <summary>The group's ban list entry for one person, and the ban and unban entries in the audit log.</summary>
internal sealed class PersonBansTool : ReadTool
{
    public override string Name => "get_person_bans";

    public override string Label => "Person's bans";

    public override string Description =>
        "Whether one person is on the group's ban list now or was before, and every ban and unban "
        + "the audit log recorded for them with who did it.";

    protected override string Schema => """
        {"type":"object","properties":{"userId":{"type":"string","description":"The VRChat user id."}},"required":["userId"]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAuditLog;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        if (ChatArguments.Text(arguments, "userId") is not { } id)
            return ChatToolResult.Problem("userId is required.");

        var db = Get<ModbotContext>(context);
        var clock = Get<IModbotClock>(context);

        var list = await MemberEndpoints.ListBansAsync(db, clock, id, "all", 1, 20, ct);
        var onList = list.Bans
            .Where(b => string.Equals(b.UserId, id, StringComparison.Ordinal))
            .Select(b => new { b.BannedAt, firstSeenOnListAt = b.FirstSeenAt, b.LiftedAt })
            .ToList();

        var history = await AuditSearch.EntriesAsync(
            context, [FactType.MemberBanned, FactType.MemberUnbanned], id, null, null, null, 50, ct);

        return ChatToolResult.Json(
            new
            {
                userId = id,
                banListCoverage = new { list.Coverage.FirstSweepComplete, list.Coverage.LastSyncedAt },
                banList = onList,
                auditLog = history.Select(AuditSearch.Summary),
            },
            [Person(id, list.Bans.FirstOrDefault(b => b.UserId == id)?.DisplayName), .. AuditSearch.References(history)]);
    }
}

/// <summary>The Members page's search.</summary>
internal sealed class SearchMembersTool : ReadTool
{
    public override string Name => "search_members";

    public override string Label => "Search members";

    public override string Description =>
        "Search the group's member list by part of a display name or a user id. Returns roles, "
        + "join date and when they were last seen, plus the member count and when the list was "
        + "last synced.";

    protected override string Schema => """
        {"type":"object","properties":{"query":{"type":"string","description":"Part of a display name or a user id. Leave out to list the newest members."},"status":{"type":"string","enum":["current","left","all"],"description":"Default current."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewMembers;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var status = ChatArguments.Text(arguments, "status") is "left" or "all" ? ChatArguments.Text(arguments, "status") : null;

        var list = await MemberEndpoints.ListMembersAsync(
            Get<ModbotContext>(context),
            Get<IModbotClock>(context),
            ChatArguments.Text(arguments, "query"),
            role: null,
            status,
            sort: null,
            page: 1,
            pageSize: ChatArguments.Number(arguments, "limit", 20, 1, 50),
            ct);

        return ChatToolResult.Json(
            new
            {
                matching = list.Total,
                memberCount = list.Coverage.MemberCount,
                list.Coverage.FirstSweepComplete,
                list.Coverage.LastSyncedAt,
                members = list.Members.Select(m => new
                {
                    m.UserId,
                    m.DisplayName,
                    roles = m.RoleNames,
                    m.JoinedAt,
                    m.LeftAt,
                    m.LastSeenAt,
                    eighteenPlusVerified = m.EighteenPlus,
                }),
            },
            list.Members.Select(m => Person(m.UserId, m.DisplayName)));
    }
}
