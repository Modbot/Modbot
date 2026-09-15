using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Chat;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>
/// What AI moderation flagged, read the way the Flags page reads it.
/// </summary>
/// <remarks>
/// Flags need <see cref="ModbotPermissions.ViewProfile"/>, which is what the Flags page needs. A
/// flag quotes the one line a rule matched, which the page already shows; reading everything
/// somebody wrote is a different permission and a different tool.
/// </remarks>
internal static class FlagRows
{
    public const string Open = "open";
    public const string Dismissed = "dismissed";

    public static IQueryable<ModerationFlag> InState(IQueryable<ModerationFlag> flags, string? state) => state switch
    {
        Dismissed => flags.Where(f => f.State == ModerationFlagState.Dismissed),
        "all" => flags,
        _ => flags.Where(f => f.State == ModerationFlagState.Open),
    };

    public static object Row(ModerationFlag f) => new
    {
        flagId = f.Id,
        f.FlaggedAt,
        rule = f.RuleName,
        f.RuleKind,
        f.Term,
        f.Target,
        subject = new
        {
            platform = f.SubjectPlatform.ToString(),
            id = f.SubjectId,
            name = f.SubjectName,
        },
        f.ChannelId,
        f.MessageId,
        matched = f.Matched,
        f.Reason,
        f.MessageDeleted,
        f.TimedOutMinutes,
        state = f.State == ModerationFlagState.Dismissed ? Dismissed : Open,
        f.DismissedAt,
        dismissedBy = f.DismissedByUsername,
    };

    public static IEnumerable<ChatReference> References(IEnumerable<ModerationFlag> flags)
    {
        foreach (var f in flags)
        {
            if (f.SubjectPlatform == FactPlatform.Discord)
            {
                yield return new ChatReference(ChatReference.DiscordPerson, f.SubjectId, f.SubjectName);

                if (f.MessageId is { } message)
                    yield return new ChatReference(ChatReference.Message, message, f.SubjectName, f.SubjectId);
            }
            else if (f.SubjectPlatform == FactPlatform.VRChat)
            {
                yield return new ChatReference(ChatReference.Person, f.SubjectId, f.SubjectName);
            }
        }
    }
}

/// <summary>One person's AI moderation flags, open and dismissed.</summary>
internal sealed class PersonFlagsTool : ReadTool
{
    public override string Name => "get_person_flags";

    public override string Label => "Person's flags";

    public override string Description =>
        "The AI moderation flags recorded against one person, newest first: which rule matched, "
        + "what it matched, whether the message was deleted or they were timed out, and whether a "
        + "moderator dismissed the flag and who. Give either a VRChat user id or a Discord account "
        + "id.";

    protected override string Schema => """
        {"type":"object","properties":{"userId":{"type":"string","description":"A VRChat user id."},"discordUserId":{"type":"string","description":"A Discord account id."},"state":{"type":"string","enum":["open","dismissed","all"],"description":"Default all."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."}},"required":[]}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewProfile;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var vrchatUserId = ChatArguments.Text(arguments, "userId");
        var discordUserId = ChatArguments.Text(arguments, "discordUserId");

        if (vrchatUserId is null && discordUserId is null)
            return ChatToolResult.Problem("Give either userId or discordUserId.");

        var limit = ChatArguments.Number(arguments, "limit", 20, 1, 50);
        var state = ChatArguments.Text(arguments, "state") ?? "all";

        var db = Get<ModbotContext>(context);

        var wanted = db.ModerationFlags.AsNoTracking()
            .Where(f => (vrchatUserId != null && f.SubjectPlatform == FactPlatform.VRChat && f.SubjectId == vrchatUserId)
                        || (discordUserId != null && f.SubjectPlatform == FactPlatform.Discord && f.SubjectId == discordUserId));

        var (rows, more) = FirstOf(
            await FlagRows.InState(wanted, state)
                .OrderByDescending(f => f.FlaggedAt)
                .Take(limit + 1)
                .ToListAsync(ct),
            limit);

        var open = await wanted.CountAsync(f => f.State == ModerationFlagState.Open, ct);

        return ChatToolResult.Json(
            new { count = rows.Count, more, openNow = open, flags = rows.Select(FlagRows.Row) },
            FlagRows.References(rows));
    }
}

/// <summary>The Flags page's list: what AI moderation flagged across the group.</summary>
internal sealed class RecentFlagsTool : ReadTool
{
    public override string Name => "recent_flags";

    public override string Label => "Recent flags";

    public override string Description =>
        "The most recent AI moderation flags across the whole group, newest first, with how many "
        + "are still open. Filter to open or dismissed ones.";

    protected override string Schema => """
        {"type":"object","properties":{"state":{"type":"string","enum":["open","dismissed","all"],"description":"Default open."},"days":{"type":"integer","minimum":1,"maximum":365,"description":"Only flags from the last this many days."},"limit":{"type":"integer","minimum":1,"maximum":50,"description":"Default 20."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewProfile;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var limit = ChatArguments.Number(arguments, "limit", 20, 1, 50);
        var days = ChatArguments.Number(arguments, "days", 0, 0, 365);
        var state = ChatArguments.Text(arguments, "state") ?? FlagRows.Open;

        var db = Get<ModbotContext>(context);
        var flags = db.ModerationFlags.AsNoTracking();

        if (days > 0)
        {
            var since = Get<IModbotClock>(context).UtcNow.AddDays(-days);
            flags = flags.Where(f => f.FlaggedAt >= since);
        }

        var (rows, more) = FirstOf(
            await FlagRows.InState(flags, state)
                .OrderByDescending(f => f.State == ModerationFlagState.Dismissed ? f.DismissedAt : f.FlaggedAt)
                .Take(limit + 1)
                .ToListAsync(ct),
            limit);

        var open = await db.ModerationFlags.AsNoTracking().CountAsync(f => f.State == ModerationFlagState.Open, ct);

        return ChatToolResult.Json(
            new { count = rows.Count, more, openNow = open, flags = rows.Select(FlagRows.Row) },
            FlagRows.References(rows));
    }
}
