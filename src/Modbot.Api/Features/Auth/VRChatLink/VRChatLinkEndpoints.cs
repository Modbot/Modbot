using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat;
using Modbot.VRChat.Users;
using VRChat.API.Model;

namespace Modbot.Api.Features.Auth.VRChatLink;

/// <summary>A link in progress: the id they pasted, the code they were given, and what is left.</summary>
public sealed record PendingLink(string VRChatUserId, string Code, DateTimeOffset ExpiresAt, int ChecksLeft);

/// <param name="ProfileUrl">Where the button sends them to find their user id. Fixed.</param>
public sealed record VRChatLinkStatus(
    bool Linked,
    string? VRChatUserId,
    string? VRChatDisplayName,
    DateTimeOffset? LinkedAt,
    PendingLink? Pending,
    string ProfileUrl);

/// <param name="UserIdOrUrl">Their user id, or the URL of their profile page. Either is fine.</param>
public sealed record StartLinkRequest(string UserIdOrUrl);

/// <param name="Linked">True when the code was found in the bio and the link is now confirmed.</param>
/// <param name="Message">What to tell them, whichever way it went.</param>
public sealed record LinkCheckResult(bool Linked, string Message, VRChatLinkStatus Status);

/// <summary>
/// Linking a VRChat account to a Modbot account (accounts and access design §4.3), by proving
/// control of it: put a code Modbot gives you in your bio, and Modbot reads it back.
/// </summary>
/// <remarks>
/// <para>
/// Reachable by an account that has not linked yet -- that is the point -- so these take the
/// signed-in policy rather than the default one.
/// </para>
/// <para>
/// The read is <see cref="VRChatBioCheck"/>, shared with the Discord account link: one profile
/// fetch through <see cref="IVRChatGate"/> on <c>users.read</c>, interactive priority. A 429 is a
/// cold stop and nothing here retries it; the person is told to try again later. Check is limited
/// per code and per account so the button cannot be used to hammer the endpoint.
/// </para>
/// <para>
/// <strong>The id is never validated</strong> (spec 3.1.1). A URL is split by <c>/</c> and the
/// last non-empty segment taken; anything else is used as typed.
/// </para>
/// </remarks>
public static class VRChatLinkEndpoints
{
    public const string ProfileUrl = "https://vrchat.com/home/user/me";

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MinimumGapBetweenChecks = TimeSpan.FromSeconds(10);
    public const int MaxChecksPerCode = 6;

    /// <summary>No 0/O or 1/I/L: the person is going to type this into a bio by hand.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static IEndpointRouteBuilder MapVRChatLink(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/auth/vrchat-link")
            .WithTags("Auth")
            .RequireAuthorization(ModbotAuth.SignedInPolicy);

        group.MapGet("/", async (
                [FromServices] UserAccountService accounts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                return user is null ? Results.Unauthorized() : Results.Ok(StatusOf(user, clock.UtcNow));
            })
            .WithName("GetVRChatLink")
            .WithSummary("Whether your VRChat account is linked, and any link in progress")
            .Produces<VRChatLinkStatus>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/start", async (
                [FromBody] StartLinkRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                var vrchatUserId = ParseUserIdOrUrl(body.UserIdOrUrl);
                if (vrchatUserId is null)
                    return Results.BadRequest(new { error = "Paste your VRChat user id, or the address of your profile page." });

                var now = clock.UtcNow;

                // A new start replaces whatever was pending: new id, new code, fresh count.
                user.VRChatLinkPendingUserId = vrchatUserId;
                user.VRChatLinkCode = NewCode();
                user.VRChatLinkCodeExpiresAt = now + CodeLifetime;
                user.VRChatLinkChecks = 0;
                user.VRChatLinkLastCheckAt = null;
                await db.SaveChangesAsync(ct);

                return Results.Ok(StatusOf(user, now));
            })
            .WithName("StartVRChatLink")
            .WithSummary("Say which VRChat account is yours and get a code to put in its bio")
            .WithDescription("The code is good for 30 minutes and for six checks. Starting again replaces it.")
            .Produces<VRChatLinkStatus>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/check", async (
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] VRChatBioCheck bioCheck,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                var now = clock.UtcNow;

                if (user.VRChatLinkCode is null || user.VRChatLinkPendingUserId is null
                    || user.VRChatLinkCodeExpiresAt is not { } expires)
                {
                    return Results.BadRequest(new { error = "Start by pasting your VRChat user id." });
                }

                if (now >= expires)
                    return Results.BadRequest(new { error = "That code has expired." });

                if (user.VRChatLinkChecks >= MaxChecksPerCode)
                    return Results.BadRequest(new { error = "That code has been checked too many times." });

                if (user.VRChatLinkLastCheckAt is { } last && now - last < MinimumGapBetweenChecks)
                {
                    var wait = (int)Math.Ceiling((MinimumGapBetweenChecks - (now - last)).TotalSeconds);
                    return Results.Json(
                        new { error = $"Wait {wait} more second{(wait == 1 ? "" : "s")} before checking again." },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

                // Counted before the call, so a call that fails still cost a check.
                user.VRChatLinkChecks++;
                user.VRChatLinkLastCheckAt = now;
                await db.SaveChangesAsync(ct);

                var pendingId = user.VRChatLinkPendingUserId;

                var check = await bioCheck.CheckAsync(pendingId, user.VRChatLinkCode, ct);

                if (!check.Read)
                    return Results.Ok(new LinkCheckResult(false, check.Problem!, StatusOf(user, now)));

                var profile = check.Profile;

                if (!check.CodeFound)
                {
                    return Results.Ok(new LinkCheckResult(
                        false,
                        $"{user.VRChatLinkCode} is not in that account's bio yet.",
                        StatusOf(user, now)));
                }

                var takenBy = await db.Users.AsNoTracking()
                    .Where(u => u.VRChatUserId == pendingId && u.Id != user.Id)
                    .Select(u => u.Username)
                    .FirstOrDefaultAsync(ct);

                if (takenBy is not null)
                {
                    return Results.Ok(new LinkCheckResult(
                        false,
                        $"That VRChat account is already linked to \"{takenBy}\".",
                        StatusOf(user, now)));
                }

                var previous = user.VRChatUserId;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                user.VRChatUserId = pendingId;
                user.VRChatDisplayName = profile?.DisplayName;
                user.VRChatLinkedAt = now;
                user.VRChatLinkCode = null;
                user.VRChatLinkCodeExpiresAt = null;
                user.VRChatLinkPendingUserId = null;
                user.VRChatLinkChecks = 0;
                user.VRChatLinkLastCheckAt = null;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.VRChatLinked,
                    user,
                    new Actor(user.Id, user.Username),
                    new JsonObject
                    {
                        ["vrchatUserId"] = pendingId,
                        ["vrchatDisplayName"] = profile?.DisplayName,
                        ["replaced"] = previous,
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new LinkCheckResult(
                    true,
                    $"Linked to {profile?.DisplayName ?? pendingId}.",
                    StatusOf(user, now)));
            })
            .WithName("CheckVRChatLink")
            .WithSummary("Read the bio and confirm the link if the code is there")
            .WithDescription(
                "One profile fetch through the gate on users.read. Six checks per code, at most one "
                + "every ten seconds. A failed fetch -- rate limit, Cloudflare, network -- is reported "
                + "in words and never retried.")
            .Produces<LinkCheckResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);

        return app;
    }

    /// <summary>
    /// The user id out of whatever was pasted. A URL gives up its last non-empty path segment;
    /// anything else is taken as typed. <strong>Never validated</strong> (spec 3.1.1).
    /// </summary>
    public static string? ParseUserIdOrUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim();

        // A profile address pasted without its scheme is still an address.
        if (!text.Contains("://", StringComparison.Ordinal)
            && (text.StartsWith("vrchat.com/", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("www.vrchat.com/", StringComparison.OrdinalIgnoreCase)))
        {
            text = "https://" + text;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
            return text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        // The path's last non-empty segment, by delimiters only. The host on its own is not an id.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : Uri.UnescapeDataString(segments[^1]);
    }

    public static string NewCode()
    {
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];

        return "modbot-" + new string(chars);
    }

    private static VRChatLinkStatus StatusOf(ModbotUser user, DateTimeOffset now)
    {
        PendingLink? pending = null;

        if (user.VRChatLinkCode is { } code
            && user.VRChatLinkPendingUserId is { } id
            && user.VRChatLinkCodeExpiresAt is { } expires
            && now < expires)
        {
            pending = new PendingLink(id, code, expires, Math.Max(0, MaxChecksPerCode - user.VRChatLinkChecks));
        }

        return new VRChatLinkStatus(
            user.IsVRChatLinked,
            user.VRChatUserId,
            user.VRChatDisplayName,
            user.VRChatLinkedAt,
            pending,
            ProfileUrl);
    }
}

