using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modbot.AI;
using Modbot.AI.Calls;
using Modbot.AI.Chat;
using Modbot.AI.Usage;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.Api.Features.Chat;

public sealed record ChatConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt);

/// <param name="Available">Whether a message would get an answer: AI is on and Chat is on.</param>
/// <param name="Model">The model Chat is set to use, for the label under the message box.</param>
public sealed record ChatHome(bool Available, string? Model, IReadOnlyList<ChatConversationSummary> Conversations);

public sealed record ChatToolCallView(string Id, string Name, string Label, string Arguments);

/// <param name="Role"><c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="ParentId">The message this one follows. Null for the first message.</param>
/// <param name="Versions">
/// Every version of this message, oldest first, this one among them. One entry means it was never
/// asked again or edited.
/// </param>
/// <param name="Stopped">True for a reply that was stopped or cut off part-written.</param>
/// <param name="ToolCallId">For a tool result: the call in an earlier assistant message it answers.</param>
public sealed record ChatMessageView(
    long Id,
    long? ParentId,
    IReadOnlyList<long> Versions,
    string Role,
    string Content,
    IReadOnlyList<ChatToolCallView> ToolCalls,
    string? ToolCallId,
    string? ToolName,
    string? ToolLabel,
    IReadOnlyList<ChatReference> References,
    bool? Worked,
    int? DurationMs,
    bool Stopped,
    DateTimeOffset CreatedAt);

/// <param name="Full">True when no more messages fit; a new conversation is needed.</param>
public sealed record ChatConversationView(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Full,
    IReadOnlyList<ChatMessageView> Messages);

/// <summary>
/// Asks a question, asks the last one again, or sends an edited question.
/// </summary>
/// <param name="ConversationId">Null starts a new conversation.</param>
/// <param name="ReplaceMessageId">
/// A question of this conversation that <paramref name="Text"/> is the edited version of. The
/// edited question is kept beside the old one rather than instead of it.
/// </param>
/// <param name="RetryAfterMessageId">
/// Write another reply to this message. <paramref name="Text"/> is not used: nothing is asked
/// again, the reply is. The old reply is kept beside the new one.
/// </param>
public sealed record ChatSendRequest(Guid? ConversationId, string? Text, long? ReplaceMessageId, long? RetryAfterMessageId);

public sealed record ChatTitleRequest(string? Title);

/// <param name="MessageId">The version to read. Everything after it is the newest run from there.</param>
public sealed record ChatVersionRequest(long MessageId);

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
/// <strong>Messages make a tree, and one line through it is shown.</strong> Every message names the
/// message it follows, and the conversation names the last message of the version being read.
/// Asking again or editing a question adds a message beside the old one, under the same parent, so
/// nothing a moderator read is ever thrown away. Everything else -- reading, sending, the message
/// count -- works on the line from the first message to that last one.
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
    public const int MaxTitleLength = ChatTitle.MaxLength;

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
                    .Select(s => new { s.AiEnabled, s.AiChatEnabled, s.AiModel, s.AiChatModel })
                    .FirstOrDefaultAsync(ct);

                var conversations = await db.AiChatConversations.AsNoTracking()
                    .Where(c => c.UserId == userId)
                    .OrderByDescending(c => c.UpdatedAt)
                    .Take(ConversationsListed)
                    .Select(c => new ChatConversationSummary(c.Id, c.Title, c.UpdatedAt))
                    .ToListAsync(ct);

                var model = settings is null
                    ? null
                    : string.IsNullOrWhiteSpace(settings.AiChatModel) ? settings.AiModel : settings.AiChatModel.Trim();

                return Results.Ok(new ChatHome(
                    settings is { AiEnabled: true, AiChatEnabled: true },
                    string.IsNullOrWhiteSpace(model) ? null : model,
                    conversations));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("GetChat")
            .WithSummary("Whether Chat answers, the model it uses, and your conversations, newest first")
            .Produces<ChatHome>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/conversations/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                CancellationToken ct) =>
            {
                var conversation = await OwnAsync(db, http, id, ct);
                return conversation is null ? Results.NotFound() : Results.Ok(View(conversation, registry));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("GetChatConversation")
            .WithSummary("One of your conversations, the version being read, in order")
            .Produces<ChatConversationView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/conversations/{id:guid}/title", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] ChatTitleRequest body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var title = body.Title?.Trim() ?? string.Empty;
                if (title.Length == 0)
                    return Results.BadRequest(new { error = "Type a name." });
                if (title.Length > MaxTitleLength)
                    return Results.BadRequest(new { error = $"A name can be at most {MaxTitleLength} characters." });

                var conversation = await OwnAsync(db, http, id, ct);
                if (conversation is null)
                    return Results.NotFound();

                conversation.Title = title;
                await db.SaveChangesAsync(ct);

                return Results.Ok(new ChatConversationSummary(conversation.Id, conversation.Title, conversation.UpdatedAt));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("RenameChatConversation")
            .WithSummary("Rename one of your conversations")
            .Produces<ChatConversationSummary>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/conversations/{id:guid}/version", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] ChatVersionRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var conversation = await OwnAsync(db, http, id, ct);
                if (conversation is null)
                    return Results.NotFound();

                var chosen = conversation.Messages.FirstOrDefault(m => m.Id == body.MessageId);
                if (chosen is null)
                    return Results.NotFound();

                conversation.LeafId = LastAfter(conversation, chosen).Id;
                await db.SaveChangesAsync(ct);

                return Results.Ok(View(conversation, registry));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("ReadChatVersion")
            .WithSummary("Read another version of a message, and the reply that followed it")
            .Produces<ChatConversationView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/conversations/{id:guid}/spend", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var conversation = await OwnAsync(db, http, id, ct);
                return conversation is null ? Results.NotFound() : Results.Ok(await SpentAsync(db, conversation, ct));
            })
            .RequiresFlag(ModbotPermissions.UseAiChat)
            .WithName("GetChatConversationSpend")
            .WithSummary("What this conversation has used and cost, every version of it included")
            .Produces<AiSpent>()
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
                "Events: `conversation` {id, title, updatedAt}, sent again at the end when the model "
                + "named it; `message` for each stored message, the question first; `text` {text} as "
                + "the reply is written; `tool` {callId, name, label} as a tool starts; `done` "
                + "{outcome, error} last. Refused before streaming with 400 for an empty or too long "
                + "message, 404 for a conversation or message that is not yours, 409 when Chat is off "
                + "or the conversation is full, and 429 when a spend limit is reached.")
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
        [FromServices] IAiCallLog calls,
        [FromServices] IModbotClock clock,
        [FromServices] IFactWriter facts,
        [FromServices] EventPartitionMaintainer partitions,
        [FromServices] IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var userId = ModbotAuth.UserIdOf(http.User)!.Value;
        var username = ModbotAuth.UsernameOf(http.User);
        var held = ModbotAuth.PermissionsOf(http.User);

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

        // Asking the same question again writes another reply beside the old one, so it sends no
        // text of its own; everything else does.
        var again = conversation is not null && body.RetryAfterMessageId is not null;

        var text = body.Text?.Trim() ?? string.Empty;
        if (!again)
        {
            if (text.Length == 0)
                return Results.BadRequest(new { error = "Type a message." });
            if (text.Length > ChatSettingsRules.MaxMessageLength)
                return Results.BadRequest(new { error = $"A message can be at most {ChatSettingsRules.MaxMessageLength} characters." });
        }

        // Where the new message hangs: after the message being answered again, in place of the
        // question being edited, or at the end of what is being read.
        long? parent = null;
        if (conversation is not null)
        {
            if (body.RetryAfterMessageId is { } retryAfter)
            {
                if (conversation.Messages.FirstOrDefault(m => m.Id == retryAfter) is not { } answered)
                    return Results.NotFound();

                parent = answered.Id;
            }
            else if (body.ReplaceMessageId is { } replaced)
            {
                if (conversation.Messages.FirstOrDefault(m => m.Id == replaced && m.Role == "user") is not { } edited)
                    return Results.NotFound();

                parent = edited.ParentId;
            }
            else
            {
                parent = Last(conversation)?.Id;
            }
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
        var fresh = conversation is null;

        // What the model is sent: the line of messages up to where this one hangs, and the question.
        var history = conversation is null ? [] : Upto(conversation, parent).Select(Turn).ToList();

        conversation ??= new AiChatConversation
        {
            UserId = userId,
            Title = ChatTitle.FromQuestion(text),
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (fresh)
            db.AiChatConversations.Add(conversation);

        var model = string.IsNullOrWhiteSpace(settings.AiChatModel) ? chat.Model : settings.AiChatModel.Trim();
        var client = model == chat.Model ? chat.Chat : chat.Client.GetChatClient(model);

        var json = jsonOptions.Value.SerializerOptions;

        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Writing to a browser that has gone is not a failure: the reply carries on being stored,
        // and what is stored is what the conversation says when it is next opened.
        async Task SendEvent(string name, object data)
        {
            if (http.RequestAborted.IsCancellationRequested)
                return;

            try
            {
                await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", CancellationToken.None);
                await http.Response.Body.FlushAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The browser went away mid-reply.
            }
        }

        // Not the request's token anywhere below: a message the model already produced is kept even
        // when the browser has gone, so the conversation reads the same when reopened.
        async Task<AiChatMessage> StoreAsync(ChatTurn turn, ChatTokenUsage? used)
        {
            var message = Store(conversation, turn, clock.UtcNow, parent, model, chat.Provider, used);
            await db.SaveChangesAsync(CancellationToken.None);

            parent = message.Id;
            conversation.LeafId = message.Id;
            await db.SaveChangesAsync(CancellationToken.None);

            return message;
        }

        if (!again)
        {
            var question = await StoreAsync(ChatTurn.User(text), null);
            history.Add(ChatTurn.User(text));

            await SendEvent("conversation", Summary(conversation));
            await SendEvent("message", View(conversation, question, registry));
        }
        else
        {
            await SendEvent("conversation", Summary(conversation));
        }

        var request = new ChatRequest(
            client,
            ChatPrompt.Build(settings.ManagedGroupName, settings.AiChatInstructions),
            history,
            registry.OfferedTo(held, ChatToolRegistry.ParseSwitches(settings.AiChatToolSwitches)),
            ChatSettingsRules.Limits(settings.AiChatMaxToolCalls, settings.AiChatMaxReplyTokens, settings.AiChatTimeLimitSeconds),
            new ChatToolContext(userId, held, http.RequestServices),
            conversation.Id,
            Uri.TryCreate(settings.AiEndpoint, UriKind.Absolute, out var address) ? address : null,
            model,
            chat.Provider,
            ChatPrompt.Now(now),
            chat.FallbackModel is null ? null : chat.Client.GetChatClient(chat.FallbackModel),
            chat.FallbackModel);

        // What the round that is being stored used. The loop reports it just before the message it
        // belongs to, so the conversation can add up what it cost without reading every usage row.
        ChatTokenUsage? round = null;
        var answer = string.Empty;

        // What the tools read about people, for the one fact this question records (design §13).
        var lookups = new List<ChatTurn>();

        var outcome = await loop.RunAsync(request, async e =>
        {
            switch (e)
            {
                case ChatTextEvent t:
                    await SendEvent("text", new { text = t.Text });
                    break;

                case ChatCallEvent made:
                    round = made.Usage;

                    // Not the request's token either: the provider has already charged for it, and
                    // the call log is how a moderator later sees why a reply stopped.
                    await usage.RecordAsync(
                        AiFeatures.Chat, userId, made.ModelAnswered ?? made.ModelAsked, chat.Provider, made.Usage, CancellationToken.None);

                    await calls.RecordAsync(
                        new AiCallEntry(
                            AiFeatures.Chat, made.ModelAsked, made.ModelAnswered, chat.Provider, made.Outcome, made.DurationMs,
                            made.Fallback, made.Error, made.Usage, userId, username),
                        CancellationToken.None);
                    break;

                case ChatTurnEvent turn:
                    var taken = turn.Turn.Role == ChatRole.Assistant ? round : null;
                    if (taken is not null)
                        round = null;

                    if (turn.Turn.Role == ChatRole.Assistant && turn.Turn.ToolCalls.Count == 0 && turn.Turn.Content.Length > 0)
                        answer = turn.Turn.Content;

                    if (turn.Turn.Role == ChatRole.Tool)
                        lookups.Add(turn.Turn);

                    var stored = await StoreAsync(turn.Turn, taken);
                    await SendEvent("message", View(conversation, stored, registry));
                    break;

                case ChatFinishedEvent done:
                    await SendEvent("done", new { outcome = done.Outcome.ToString(), error = done.Error });
                    break;
            }
        }, ct);

        // Who asked about whom, once for the whole question. Written after the reply rather than
        // inside each tool, so asking about one person six ways is one entry in their history --
        // and written whatever the outcome, because a tool that read somebody read them even if
        // the reply was stopped afterwards.
        var (people, toolsUsed) = ChatLookupFacts.Read(lookups);
        await ChatLookupFacts.RecordAsync(
            facts,
            partitions,
            clock.UtcNow,
            Actor.Of(http) ?? new Actor(userId, string.Empty),
            conversation.Id,
            people,
            toolsUsed,
            CancellationToken.None);

        // A name for the list, written once, from the exchange that started the conversation. The
        // first words of the question are already stored, so a provider that refuses costs nothing.
        if (fresh && outcome != ChatOutcome.Cancelled && answer.Length > 0)
        {
            var named = await ChatTitle.SuggestAsync(client, chat.Provider, text, answer, CancellationToken.None);

            if (named.Usage is not null)
                await usage.RecordAsync(AiFeatures.Chat, userId, model, chat.Provider, named.Usage, CancellationToken.None);

            if (named.Title is { Length: > 0 } title)
            {
                conversation.Title = title;
                await db.SaveChangesAsync(CancellationToken.None);
                await SendEvent("conversation", Summary(conversation));
            }
        }

        return Results.Empty;
    }

    // ── The line being read ──────────────────────────────────────────────────────────────────

    /// <summary>The conversation, if it is this caller's.</summary>
    private static Task<AiChatConversation?> OwnAsync(ModbotContext db, HttpContext http, Guid id, CancellationToken ct)
    {
        var userId = ModbotAuth.UserIdOf(http.User)!.Value;

        return db.AiChatConversations
            .Include(c => c.Messages.OrderBy(m => m.Id))
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
    }

    /// <summary>The last message of the version being read.</summary>
    /// <remarks>
    /// A conversation stored before versions existed has no last message written down and no
    /// message names its parent, so the whole list in order is the line: it never branched.
    /// </remarks>
    private static AiChatMessage? Last(AiChatConversation conversation) =>
        conversation.LeafId is { } leaf
            ? conversation.Messages.FirstOrDefault(m => m.Id == leaf)
            : conversation.Messages.OrderBy(m => m.Id).LastOrDefault();

    /// <summary>The line from the first message to <paramref name="untilId"/>, in order.</summary>
    private static IReadOnlyList<AiChatMessage> Upto(AiChatConversation conversation, long? untilId)
    {
        if (untilId is not { } id)
            return [];

        var byId = conversation.Messages.ToDictionary(m => m.Id);
        var line = new List<AiChatMessage>();

        // A conversation from before versions: nothing names a parent, so the line is everything
        // up to and including the message asked for.
        if (conversation.LeafId is null && conversation.Messages.All(m => m.ParentId is null))
            return [.. conversation.Messages.OrderBy(m => m.Id).Where(m => m.Id <= id)];

        while (byId.TryGetValue(id, out var message))
        {
            line.Add(message);
            if (message.ParentId is not { } up)
                break;

            id = up;
        }

        line.Reverse();
        return line;
    }

    /// <summary>The line being read, first message first.</summary>
    private static IReadOnlyList<AiChatMessage> Line(AiChatConversation conversation) =>
        Upto(conversation, Last(conversation)?.Id);

    /// <summary>
    /// The end of the newest run through <paramref name="from"/>: each step takes the newest
    /// message that follows, which is the version written last.
    /// </summary>
    private static AiChatMessage LastAfter(AiChatConversation conversation, AiChatMessage from)
    {
        var message = from;

        while (conversation.Messages.Where(m => m.ParentId == message.Id).MaxBy(m => m.Id) is { } next)
            message = next;

        return message;
    }

    /// <summary>Every version of each message on the line: the messages sharing its parent.</summary>
    private static IReadOnlyList<long> VersionsOf(AiChatConversation conversation, AiChatMessage message)
    {
        // Nothing to switch between in a conversation from before versions.
        if (conversation.LeafId is null && message.ParentId is null)
            return [message.Id];

        return [.. conversation.Messages
            .Where(m => m.ParentId == message.ParentId && m.Role == message.Role)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)];
    }

    // ── Storing and showing ──────────────────────────────────────────────────────────────────

    private static AiChatMessage Store(
        AiChatConversation conversation,
        ChatTurn turn,
        DateTimeOffset now,
        long? parentId,
        string model,
        string? provider,
        ChatTokenUsage? used)
    {
        var message = new AiChatMessage
        {
            ParentId = parentId,
            Role = RoleName(turn.Role),
            Content = turn.Content,
            ToolCalls = turn.ToolCalls.Count == 0 ? null : JsonSerializer.Serialize(turn.ToolCalls, Stored),
            ToolCallId = turn.ToolCallId,
            ToolName = turn.ToolName,
            Mentioned = turn.References is { Count: > 0 } refs ? JsonSerializer.Serialize(refs, Stored) : null,
            Worked = turn.Worked,
            DurationMs = turn.DurationMs,
            Stopped = turn.Stopped,
            CreatedAt = now,
        };

        if (used is not null)
        {
            message.Model = model;
            message.InputTokens = used.InputTokenCount;
            message.OutputTokens = used.OutputTokenCount;
            message.CachedInputTokens = used.InputTokenDetails?.CachedTokenCount ?? 0;
            message.ReportedCost = AiReportedCost.Of(used, provider);
        }

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
        m.DurationMs,
        m.Stopped);

    private static ChatConversationSummary Summary(AiChatConversation c) =>
        new(c.Id, c.Title, c.UpdatedAt);

    private static ChatConversationView View(AiChatConversation c, ChatToolRegistry registry) => new(
        c.Id,
        c.Title,
        c.CreatedAt,
        c.UpdatedAt,
        c.Messages.Count >= ChatSettingsRules.MaxMessagesPerConversation,
        [.. Line(c).Select(m => View(c, m, registry))]);

    private static ChatMessageView View(AiChatConversation c, AiChatMessage m, ChatToolRegistry registry) => new(
        m.Id,
        m.ParentId,
        VersionsOf(c, m),
        m.Role,
        m.Content,
        [.. Calls(m.ToolCalls).Select(call => new ChatToolCallView(call.Id, call.Name, LabelOf(registry, call.Name), call.Arguments))],
        m.ToolCallId,
        m.ToolName,
        m.ToolName is null ? null : LabelOf(registry, m.ToolName),
        References(m.Mentioned),
        m.Worked,
        m.DurationMs,
        m.Stopped,
        m.CreatedAt);

    /// <summary>
    /// What this conversation has used and cost, every version of it included: the rounds are
    /// counted where they were written down, and priced when they are read, like all AI spend
    /// (design §10.2).
    /// </summary>
    private static async Task<AiSpent> SpentAsync(ModbotContext db, AiChatConversation conversation, CancellationToken ct)
    {
        var rounds = conversation.Messages
            .Where(m => m.InputTokens is not null || m.OutputTokens is not null)
            .GroupBy(m => (Model: m.Model ?? string.Empty, Reported: m.ReportedCost is not null))
            .Select(g => new AiUsageSum(
                g.Key.Model,
                g.Key.Reported,
                g.Sum(m => (long)(m.InputTokens ?? 0)),
                g.Sum(m => (long)(m.CachedInputTokens ?? 0)),
                g.Sum(m => (long)(m.OutputTokens ?? 0)),
                g.Sum(m => m.ReportedCost ?? 0m)))
            .ToList();

        if (rounds.Count == 0)
            return AiSpent.None;

        var prices = await AiPrices.ForAsync(db, [.. rounds.Select(r => r.Model).Distinct(StringComparer.Ordinal)], ct);
        return AiSpent.Sum(rounds.Select(r => r.PricedWith(prices)));
    }

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
}
