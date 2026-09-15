using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modbot.AI;
using Modbot.AI.Chat;
using Modbot.AI.Usage;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat;

public sealed record ChatConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt);

/// <param name="Available">Whether a message would get an answer: AI is on and Chat is on.</param>
public sealed record ChatHome(bool Available, IReadOnlyList<ChatConversationSummary> Conversations);

public sealed record ChatToolCallView(string Id, string Name, string Label, string Arguments);

/// <param name="Role"><c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="ToolCallId">For a tool result: the call in an earlier assistant message it answers.</param>
public sealed record ChatMessageView(
    long Id,
    string Role,
    string Content,
    IReadOnlyList<ChatToolCallView> ToolCalls,
    string? ToolCallId,
    string? ToolName,
    string? ToolLabel,
    IReadOnlyList<ChatReference> References,
    bool? Worked,
    int? DurationMs,
    DateTimeOffset CreatedAt);

/// <param name="Full">True when no more messages fit; a new conversation is needed.</param>
public sealed record ChatConversationView(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Full,
    IReadOnlyList<ChatMessageView> Messages);

/// <param name="ConversationId">Null starts a new conversation.</param>
public sealed record ChatSendRequest(Guid? ConversationId, string? Text);

/// <summary>
/// The Chat page (AI chat design): the person's own conversations, and sending a message.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A conversation belongs to one account.</strong> Every read, delete and send filters on
/// the caller's own id, and a conversation that belongs to somebody else answers 404 exactly like
/// one that does not exist -- a 403 would confirm it is there.
/// </para>
/// <para>
/// Sending answers with <c>text/event-stream</c>: <c>conversation</c>, then <c>message</c> for the
/// stored question, then any number of <c>text</c>, <c>tool</c> and <c>message</c> events as the
/// reply is written, and <c>done</c> last. Every message is stored before its event is sent, so a
/// browser that goes away mid-reply loses nothing that was already said.
/// </para>
/// </remarks>
public static class ChatEndpoints
{
    public const int ConversationsListed = 100;
    public const int MaxTitleLength = 80;

    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/chat").WithTags("Chat").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var userId = ModbotAuth.UserIdOf(http.User)!.Value;

                var settings = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => new { s.AiEnabled, s.AiChatEnabled })
                    .FirstOrDefaultAsync(ct);

                var conversations = await db.AiChatConversations.AsNoTracking()
                    .Where(c => c.UserId == userId)
                    .OrderByDescending(c => c.UpdatedAt)
                    .Take(ConversationsListed)
                    .Select(c => new ChatConversationSummary(c.Id, c.Title, c.UpdatedAt))
                    .ToListAsync(ct);

                return Results.Ok(new ChatHome(settings is { AiEnabled: true, AiChatEnabled: true }, conversations));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("GetChat")
            .WithSummary("Whether Chat answers, and your conversations, newest first")
            .Produces<ChatHome>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/conversations/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                CancellationToken ct) =>
            {
                var userId = ModbotAuth.UserIdOf(http.User)!.Value;

                var conversation = await db.AiChatConversations.AsNoTracking()
                    .Include(c => c.Messages.OrderBy(m => m.Id))
                    .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

                return conversation is null ? Results.NotFound() : Results.Ok(View(conversation, registry));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("GetChatConversation")
            .WithSummary("One of your conversations, every message in order")
            .Produces<ChatConversationView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/conversations/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var userId = ModbotAuth.UserIdOf(http.User)!.Value;

                var deleted = await db.AiChatConversations
                    .Where(c => c.Id == id && c.UserId == userId)
                    .ExecuteDeleteAsync(ct);

                return deleted == 0 ? Results.NotFound() : Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("DeleteChatConversation")
            .WithSummary("Delete one of your conversations")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/messages", SendAsync)
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("SendChatMessage")
            .WithSummary("Ask a question; the reply streams back as server-sent events")
            .WithDescription(
                "Events: `conversation` {id, title}; `message` for each stored message, the question "
                + "first; `text` {text} as the reply is written; `tool` {callId, name, label} as a "
                + "tool starts; `done` {outcome, error} last. Refused before streaming with 400 for an "
                + "empty or too long message, 404 for a conversation that is not yours, 409 when "
                + "Chat is off or the conversation is full, and 429 when a spend limit is reached.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> SendAsync(
        HttpContext http,
        [FromBody] ChatSendRequest body,
        [FromServices] ModbotContext db,
        [FromServices] IAiClients ai,
        [FromServices] ChatToolRegistry registry,
        [FromServices] ChatLoop loop,
        [FromServices] AiSpendLimits limits,
        [FromServices] IAiUsage usage,
        [FromServices] IModbotClock clock,
        [FromServices] IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var userId = ModbotAuth.UserIdOf(http.User)!.Value;
        var held = ModbotAuth.PermissionsOf(http.User);

        var text = body.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return Results.BadRequest(new { error = "Type a message." });
        if (text.Length > ChatSettingsRules.MaxMessageLength)
            return Results.BadRequest(new { error = $"A message can be at most {ChatSettingsRules.MaxMessageLength} characters." });

        AiChatConversation? conversation = null;
        if (body.ConversationId is { } conversationId)
        {
            conversation = await db.AiChatConversations
                .Include(c => c.Messages.OrderBy(m => m.Id))
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);

            if (conversation is null)
                return Results.NotFound();

            if (conversation.Messages.Count >= ChatSettingsRules.MaxMessagesPerConversation)
                return Results.Conflict(new { error = "This conversation is full. Start a new one." });
        }

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is not { AiEnabled: true, AiChatEnabled: true })
            return Results.Conflict(new { error = "Chat is off." });

        var chat = await ai.GetChatAsync(ct);
        if (chat is null)
            return Results.Conflict(new { error = "AI is not set up." });

        // Checked before the turn starts, never in the middle of one (AI chat design §10): the limit
        // for everyone, Chat's own, and the ones set on this person or their roles.
        if (await limits.CheckAsync(AiFeatures.Chat, userId, held, ct) is { } reached)
            return Results.Json(new { error = reached.Message }, statusCode: StatusCodes.Status429TooManyRequests);

        var now = clock.UtcNow;
        var history = conversation?.Messages.Select(Turn).ToList() ?? [];

        if (conversation is null)
        {
            conversation = new AiChatConversation { UserId = userId, Title = Title(text), CreatedAt = now, UpdatedAt = now };
            db.AiChatConversations.Add(conversation);
        }

        var question = Store(conversation, ChatTurn.User(text), now);
        await db.SaveChangesAsync(ct);
        history.Add(ChatTurn.User(text));

        var model = string.IsNullOrWhiteSpace(settings.AiChatModel) ? chat.Model : settings.AiChatModel.Trim();
        var client = model == chat.Model ? chat.Chat : chat.Client.GetChatClient(model);

        var request = new ChatRequest(
            client,
            ChatPrompt.Build(now, settings.ManagedGroupName, settings.AiChatInstructions),
            history,
            registry.OfferedTo(held, ChatToolRegistry.ParseSwitches(settings.AiChatToolSwitches)),
            ChatSettingsRules.Limits(settings.AiChatMaxToolCalls, settings.AiChatMaxReplyTokens, settings.AiChatTimeLimitSeconds),
            new ChatToolContext(userId, held, http.RequestServices),
            conversation.Id,
            Uri.TryCreate(settings.AiEndpoint, UriKind.Absolute, out var address) ? address : null,
            model,
            chat.Provider);

        var json = jsonOptions.Value.SerializerOptions;

        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        async Task SendEvent(string name, object data)
        {
            await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        await SendEvent("conversation", new ChatConversationSummary(conversation.Id, conversation.Title, conversation.UpdatedAt));
        await SendEvent("message", View(question, registry));

        await loop.RunAsync(request, async e =>
        {
            switch (e)
            {
                case ChatTextEvent t:
                    await SendEvent("text", new { text = t.Text });
                    break;

                case ChatUsageEvent used:
                    // Not the request's token either: the provider has already charged for it.
                    await usage.RecordAsync(AiFeatures.Chat, userId, used.Model, chat.Provider, used.Usage, CancellationToken.None);
                    break;

                case ChatToolStartedEvent s:
                    await SendEvent("tool", new { callId = s.CallId, name = s.Tool, label = LabelOf(registry, s.Tool) });
                    break;

                case ChatTurnEvent turn:
                    var stored = Store(conversation, turn.Turn, clock.UtcNow);

                    // Not the request's token: a message the model already produced is kept even
                    // when the browser has gone, so the conversation reads the same when reopened.
                    await db.SaveChangesAsync(CancellationToken.None);
                    await SendEvent("message", View(stored, registry));
                    break;

                case ChatFinishedEvent done:
                    await SendEvent("done", new { outcome = done.Outcome.ToString(), error = done.Error });
                    break;
            }
        }, ct);

        return Results.Empty;
    }

    private static AiChatMessage Store(AiChatConversation conversation, ChatTurn turn, DateTimeOffset now)
    {
        var message = new AiChatMessage
        {
            Role = RoleName(turn.Role),
            Content = turn.Content,
            ToolCalls = turn.ToolCalls.Count == 0 ? null : JsonSerializer.Serialize(turn.ToolCalls, Stored),
            ToolCallId = turn.ToolCallId,
            ToolName = turn.ToolName,
            Mentioned = turn.References is { Count: > 0 } refs ? JsonSerializer.Serialize(refs, Stored) : null,
            Worked = turn.Worked,
            DurationMs = turn.DurationMs,
            CreatedAt = now,
        };

        conversation.Messages.Add(message);
        conversation.UpdatedAt = now;
        return message;
    }

    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web);

    private static ChatTurn Turn(AiChatMessage m) => new(
        m.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        },
        m.Content,
        Calls(m.ToolCalls),
        m.ToolCallId,
        m.ToolName,
        References(m.Mentioned),
        m.Worked,
        m.DurationMs);

    private static ChatConversationView View(AiChatConversation c, ChatToolRegistry registry) => new(
        c.Id,
        c.Title,
        c.CreatedAt,
        c.UpdatedAt,
        c.Messages.Count >= ChatSettingsRules.MaxMessagesPerConversation,
        [.. c.Messages.OrderBy(m => m.Id).Select(m => View(m, registry))]);

    private static ChatMessageView View(AiChatMessage m, ChatToolRegistry registry) => new(
        m.Id,
        m.Role,
        m.Content,
        [.. Calls(m.ToolCalls).Select(call => new ChatToolCallView(call.Id, call.Name, LabelOf(registry, call.Name), call.Arguments))],
        m.ToolCallId,
        m.ToolName,
        m.ToolName is null ? null : LabelOf(registry, m.ToolName),
        References(m.Mentioned),
        m.Worked,
        m.DurationMs,
        m.CreatedAt);

    private static IReadOnlyList<ChatToolCallRecord> Calls(string? json) =>
        Read<List<ChatToolCallRecord>>(json) ?? [];

    private static IReadOnlyList<ChatReference> References(string? json) =>
        Read<List<ChatReference>>(json) ?? [];

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Stored);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string LabelOf(ChatToolRegistry registry, string name) =>
        registry.All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal))?.Label ?? name;

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    /// <summary>The first line of the first message, cut to fit the list.</summary>
    internal static string Title(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= MaxTitleLength ? line : string.Concat(line.AsSpan(0, MaxTitleLength - 1), "…");
    }
}
