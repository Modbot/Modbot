using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI.Chat;
using Modbot.Api.Features.Feedback;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>
/// Files a bug, an idea or a change as feedback, as the person asking (feedback design §7).
/// </summary>
/// <remarks>
/// <para>
/// The one chat tool that acts, so it is off until an admin switches it on (AI chat design §3.3).
/// It saves through <see cref="FeedbackWriter"/>, the same path the companion's Send takes: the item
/// goes into this server's own table, names the asking person as its sender, and nowhere else.
/// </para>
/// <para>
/// The registry already leaves the tool out for somebody without Send feedback; it checks again
/// here, because the permission is what lets a person file feedback and not the list the model was
/// shown.
/// </para>
/// </remarks>
internal sealed class FileFeedbackTool : IChatTool
{
    public string Name => ChatPrompt.FileFeedbackToolName;

    public string Label => "File feedback";

    public string Description =>
        "Files feedback about Modbot for the admins: a bug, an idea or a change, written as the "
        + "person you are talking to. Call it only after you have shown them the draft (kind, title "
        + "and details) and they have said yes. Leave out the names of people, worlds and groups.";

    public BinaryData Parameters => BinaryData.FromString("""
        {"type":"object","properties":{"kind":{"type":"string","enum":["bug","idea","change"],"description":"bug: something is broken. idea: something new. change: something that exists should work differently."},"title":{"type":"string","maxLength":80,"description":"A short title."},"details":{"type":"string","description":"The feedback in clear sentences, without names of people, worlds or groups."}},"required":["kind","details"]}
        """);

    public ModbotPermissions Needs => ModbotPermissions.SendFeedback;

    public bool OnlyReads => false;

    public async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ChatToolRegistry.Allows(context.Held, Needs))
            return ChatToolResult.Problem("This account may not send feedback.");

        var words = FeedbackWriter.Check(
            ChatArguments.Text(arguments, "kind"),
            ChatArguments.Text(arguments, "title"),
            ChatArguments.Text(arguments, "details"),
            out var problem);
        if (words is null)
            return ChatToolResult.Problem(problem!);

        var db = context.Services.GetRequiredService<ModbotContext>();
        var username = await db.Users.AsNoTracking()
            .Where(u => u.Id == context.UserId)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var writer = new FeedbackWriter(
            db, context.Services.GetRequiredService<AccountFacts>(), context.Services.GetRequiredService<IModbotClock>());
        var item = await writer.SaveAsync(new Actor(context.UserId, username), words, ct);

        return ChatToolResult.Json(new { filed = true, id = item.Id, kind = item.Kind, title = item.Title });
    }
}
