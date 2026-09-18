using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.VRChat;
using Modbot.VRChat.Proxy;

namespace Modbot.Api.Features.Proxy;

/// <summary>
/// <c>/api/proxy/vrchat/{path}</c>: a request forwarded to <c>https://api.vrchat.cloud/{path}</c>
/// and answered with whatever VRChat said (VRChat proxy design).
/// </summary>
/// <remarks>
/// <para>
/// For a script, a tool or a VRChat client library that should use the group's service account
/// without holding its password, and for trying an endpoint from the Settings page. A caller
/// with a Modbot key goes out as the service account, through the gate, on the <c>proxy</c>
/// budget. A caller with their own VRChat cookie goes out as themselves, on
/// <c>proxy.passthrough</c>. Both are paced; neither queues in front of a sweep or a ban.
/// </para>
/// <para>
/// The switch comes first and answers 404 while it is off, before any credential is looked at,
/// so a Modbot whose operator never turned this on looks like one that does not have it. Then
/// who is calling (<see cref="VRChatProxyCallers"/>), then the body, then the gate.
/// </para>
/// <para>
/// Mapped once per method rather than as one endpoint taking four, so the API reference lists
/// four operations with four names instead of one name four times.
/// </para>
/// </remarks>
public static class VRChatProxyEndpoints
{
    public const string RoutePrefix = "/api/proxy/vrchat/";

    /// <summary>Names every forwarding operation, so the API reference can tell them apart from the rest.</summary>
    public const string OperationPrefix = "VRChatProxy";

    /// <summary>Says which account the answer came from: <c>service</c> or <c>own</c>.</summary>
    public const string AccountHeader = "X-Modbot-Proxy-Account";

    /// <summary>The most a forwarded request body may be. VRChat's API takes JSON, not uploads.</summary>
    public const int MaxRequestBodyBytes = 1024 * 1024;

    public const string SwitchedOff = "The VRChat proxy is off.";

    public static IEndpointRouteBuilder MapVRChatProxy(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Anonymous, because a VRChat cookie is not something the authentication schemes know;
        // the default scheme still runs, so a key in the header or the session cookie arrives as
        // http.User, and VRChatProxyCallers decides from there.
        var group = app.MapGroup("/api/proxy/vrchat")
            .WithTags("VRChat proxy")
            .WithMetadata(new AllowAnonymousAttribute());

        Map(group, HttpMethods.Get, "Get", "Forward a GET to VRChat");
        Map(group, HttpMethods.Post, "Post", "Forward a POST to VRChat");
        Map(group, HttpMethods.Put, "Put", "Forward a PUT to VRChat");
        Map(group, HttpMethods.Delete, "Delete", "Forward a DELETE to VRChat");

        return app;
    }

    private static void Map(RouteGroupBuilder group, string method, string name, string summary)
        => group.MapMethods("/{**path}", [method], (
                HttpContext http,
                [FromRoute] string? path,
                [FromServices] ModbotContext db,
                [FromServices] IVRChatGate gate,
                [FromServices] ApiCallers callers,
                CancellationToken ct) => ForwardAsync(http, path, db, gate, callers, ct))
            .WithName(OperationPrefix + name)
            .WithSummary(summary)
            .WithDescription(
                "Sends the request to `https://api.vrchat.cloud/{path}` and answers with VRChat's "
                + "status, body and the headers that matter. With a Modbot API key -- in the "
                + "`Authorization` header, or as the value of a cookie named `auth`, which is where "
                + "a VRChat client library puts it -- the request goes out as the group's service "
                + "account. With anything else in the `auth` cookie it goes out with your own "
                + "cookies, as you. Needs the `UseVRChatProxy` permission for the service account. "
                + "Answers 404 while the proxy is switched off, 429 when Modbot's own pacing has "
                + "stopped the proxy, and 503 when Modbot cannot reach VRChat at all.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> ForwardAsync(
        HttpContext http,
        string? path,
        ModbotContext db,
        IVRChatGate gate,
        ApiCallers callers,
        CancellationToken ct)
    {
        var on = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.VRChatProxyEnabled)
            .FirstOrDefaultAsync(ct);

        if (!on)
            return Results.Json(new { error = SwitchedOff }, statusCode: StatusCodes.Status404NotFound);

        var caller = await VRChatProxyCallers.ResolveAsync(http, callers, ct);

        switch (caller.Kind)
        {
            case VRChatProxyCallerKind.NotSignedIn:
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "Sign in with a Modbot API key, or send your own VRChat cookie." }, statusCode: StatusCodes.Status401Unauthorized);

            case VRChatProxyCallerKind.Forbidden:
                return Results.Json(new { error = caller.Reason }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { error = "The path to forward is missing." });

        var body = await ReadBodyAsync(http.Request, ct);
        if (body is null)
            return Results.Json(new { error = $"The body is larger than {MaxRequestBodyBytes} bytes." }, statusCode: StatusCodes.Status413PayloadTooLarge);

        var headers = http.Request.Headers
            .SelectMany(h => h.Value.Where(v => v is not null).Select(v => new KeyValuePair<string, string>(h.Key, v!)))
            .ToList();

        var request = new VRChatProxyRequest(
            http.Request.Method,
            path,
            http.Request.QueryString.Value ?? string.Empty,
            headers,
            body.Length == 0 ? null : body,
            http.Request.ContentType);

        // The path is the operation in the HTTP log, the way an SDK method name is elsewhere;
        // the query string is not, because a search term is not something to write down.
        var endpoint = new VRChatEndpoint(
            caller.Account == VRChatProxyAccount.Caller ? VRChatEndpointClass.ProxyPassthrough : VRChatEndpointClass.Proxy,
            null,
            $"{http.Request.Method} /{path}");

        var result = await gate.ForwardAsync(endpoint, request, caller.Account, VRChatCallPriority.Interactive, ct);

        if (!result.Success || result.Value is not { } answer)
            return NotForwarded(result);

        http.Response.StatusCode = answer.StatusCode;
        http.Response.Headers[AccountHeader] = caller.Account == VRChatProxyAccount.Caller ? "own" : "service";

        foreach (var (name, value) in answer.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;

            http.Response.Headers.Append(name, value);
        }

        if (answer.ContentType is { Length: > 0 } contentType)
            http.Response.ContentType = contentType;

        http.Response.ContentLength = answer.Body.Length;
        await http.Response.Body.WriteAsync(answer.Body, ct);

        return Results.Empty;
    }

    /// <summary>
    /// What to say when the gate did not send the request. A rate limit is a 429, because that
    /// is what a client library backs off on; everything else -- no session, a wait to sign in,
    /// the network -- is a 503 with the gate's own sentence.
    /// </summary>
    private static IResult NotForwarded(VRChatResult<VRChatProxyResponse> result)
    {
        var message = result.ErrorMessage ?? "Modbot could not reach VRChat.";

        return result.Kind is VRChatFailureKind.RateLimited
            ? Results.Json(new { error = message }, statusCode: StatusCodes.Status429TooManyRequests)
            : Results.Json(new { error = message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static bool HasBody(HttpRequest request)
        => request.ContentLength is > 0 || (request.ContentLength is null && !HttpMethods.IsGet(request.Method));

    /// <summary>The body, or null when it is larger than the proxy takes.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > MaxRequestBodyBytes)
            return null;

        if (!HasBody(request))
            return [];

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxRequestBodyBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
