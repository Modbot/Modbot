using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modbot.AI.Chat;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Modbot.Api.Features.Mcp;

/// <summary>
/// The MCP server's tools: Chat's tools, offered to whoever is calling (MCP server design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One registry, two callers.</strong> The Chat page and the MCP server read the same
/// <see cref="ChatToolRegistry"/>, under the same switches from Settings → AI → Chat, and every
/// tool runs with the same <see cref="ChatToolContext"/>: the person's own permissions, read on
/// this request. A tool the person lacks the permission for is not listed, and a call naming one
/// anyway gets "No such tool." -- the rule Chat's loop applies (AI chat design §3.1).
/// </para>
/// <para>
/// A call that read about people writes the same fact a Chat question does, so looking somebody
/// up through Claude or ChatGPT is as visible in their history as looking them up on the Chat
/// page (AI chat design §13). One per call here, because a call is the whole question.
/// </para>
/// </remarks>
internal static class McpTools
{
    private const string NoSuchTool = """{"error":"No such tool."}""";
    private const string ToolFailed = """{"error":"The tool failed."}""";

    public static async ValueTask<ListToolsResult> ListAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var http = HttpOf(request.Services);
        var offered = await OfferedAsync(http, ct);

        return new ListToolsResult { Tools = [.. offered.Select(Describe)] };
    }

    public static async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var http = HttpOf(request.Services);
        var services = http.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Modbot.Api.Features.Mcp.McpTools");
        var userId = ModbotAuth.UserIdOf(http.User)!.Value;

        var name = request.Params?.Name ?? string.Empty;
        var offered = await OfferedAsync(http, ct);
        var tool = offered.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

        if (tool is null)
        {
            logger.LogWarning("MCP tool {Tool} for user {UserId} was not offered and did not run", Shorten(name), userId);
            return Answer(NoSuchTool, worked: false);
        }

        var arguments = request.Params?.Arguments is { } given
            ? JsonSerializer.SerializeToElement(given)
            : JsonDocument.Parse("{}").RootElement;

        var context = new ChatToolContext(userId, ModbotAuth.PermissionsOf(http.User), services);
        var started = Stopwatch.GetTimestamp();
        ChatToolResult result;

        try
        {
            result = await tool.RunAsync(context, arguments, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "MCP tool {Tool} for user {UserId} threw {ErrorType}", tool.Name, userId, e.GetType().Name);
            result = new ChatToolResult(ToolFailed, [], Worked: false);
        }

        var duration = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        logger.LogInformation(
            "MCP tool {Tool} for user {UserId}: {Result} in {DurationMs} ms",
            tool.Name, userId, result.Worked ? "ok" : "failed", duration);

        var content = result.Content.Length <= ChatSettingsRules.MaxToolResultLength
            ? result.Content
            : string.Concat(result.Content.AsSpan(0, ChatSettingsRules.MaxToolResultLength), " [cut: too long]");

        // Who asked about whom, the way a Chat question records it. Not the request's token: a
        // tool that read somebody read them, whether or not the app is still listening.
        await RecordLookupAsync(http, tool.Name, result, duration, CancellationToken.None);

        return Answer(content, result.Worked);
    }

    /// <summary>The tools this caller is offered: switched on, and needing nothing they lack.</summary>
    private static async Task<IReadOnlyList<IChatTool>> OfferedAsync(HttpContext http, CancellationToken ct)
    {
        var services = http.RequestServices;
        var registry = services.GetRequiredService<ChatToolRegistry>();
        var db = services.GetRequiredService<ModbotContext>();

        var switches = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.AiChatToolSwitches)
            .FirstOrDefaultAsync(ct);

        return registry.OfferedTo(ModbotAuth.PermissionsOf(http.User), ChatToolRegistry.ParseSwitches(switches));
    }

    private static Tool Describe(IChatTool tool) => new()
    {
        Name = tool.Name,
        Title = tool.Label,
        Description = tool.Description,
        InputSchema = JsonSerializer.Deserialize<JsonElement>(tool.Parameters),
        Annotations = new ToolAnnotations
        {
            Title = tool.Label,
            ReadOnlyHint = tool.OnlyReads,
            DestructiveHint = !tool.OnlyReads,
            OpenWorldHint = false,
        },
    };

    private static CallToolResult Answer(string content, bool worked) => new()
    {
        Content = [new TextContentBlock { Text = content }],
        IsError = !worked,
    };

    private static async Task RecordLookupAsync(HttpContext http, string toolName, ChatToolResult result, int duration, CancellationToken ct)
    {
        var turn = new ChatTurn(ChatRole.Tool, result.Content, [], null, toolName, result.References, result.Worked, duration);
        var (people, tools) = ChatLookupFacts.Read([turn]);
        if (people.Count == 0)
            return;

        var services = http.RequestServices;
        var db = services.GetRequiredService<ModbotContext>();

        // What the person connected with, so their history says which app asked.
        string? client = null;
        if (McpAuthentication.GrantIdOf(http.User) is { } grantId)
        {
            client = await db.McpGrants.AsNoTracking()
                .Where(g => g.Id == grantId)
                .Select(g => g.Client!.Name)
                .FirstOrDefaultAsync(ct);
        }
        else if (ApiKeyAuthentication.KeyIdOf(http.User) is { } keyId)
        {
            client = await db.ApiKeys.AsNoTracking()
                .Where(k => k.Id == keyId)
                .Select(k => "API key " + k.Name)
                .FirstOrDefaultAsync(ct);
        }

        var clock = services.GetRequiredService<IModbotClock>();
        var facts = services.GetRequiredService<IFactWriter>();
        var partitions = services.GetRequiredService<EventPartitionMaintainer>();
        var actor = Actor.Of(http) ?? new Actor(ModbotAuth.UserIdOf(http.User)!.Value, string.Empty);

        await ChatLookupFacts.RecordAsync(
            facts,
            partitions,
            clock.UtcNow,
            actor,
            new JsonObject { ["via"] = "mcp", ["client"] = client },
            people,
            tools,
            ct);
    }

    private static HttpContext HttpOf(IServiceProvider? services)
        => services?.GetRequiredService<IHttpContextAccessor>().HttpContext
           ?? throw new InvalidOperationException("The MCP server is not serving an HTTP request.");

    private static string Shorten(string name) => name.Length <= 64 ? name : name[..64];
}
