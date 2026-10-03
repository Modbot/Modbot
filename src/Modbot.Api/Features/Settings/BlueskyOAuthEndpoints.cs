using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// The two addresses Bluesky's sign-in reaches on this Modbot (posts design §4.2c, step 3b): the
/// client document Bluesky's server fetches, and the callback the browser comes back to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The client document</strong> (<see cref="BlueskyOAuth.MetadataPath"/>) is public, as
/// atproto needs it to be, and is built from the public address setting alone, never from the
/// request. Without a public https address it is a 404. It carries the public half of Modbot's signing
/// key, never the private one.
/// </para>
/// <para>
/// <strong>The callback</strong> takes the waiting sign-in whatever happens (it is good once), and
/// finishes it only for the same signed-in person, still holding Change settings, with the state and
/// the sign-in server's <c>iss</c> it expects, and with tokens for the account the handle named
/// (<see cref="BlueskyOAuth.FinishAsync"/>). The session cookie is <c>SameSite=Lax</c>, so it comes
/// along on Bluesky's top-level redirect. It then reads the account's profile, as Check does, so
/// Posting may be turned on at once. It answers by sending the browser to Settings → Bluesky with
/// <c>?bluesky=</c> saying how it went.
/// </para>
/// <para>
/// Both are browser flows driven by Bluesky, shaped by the OAuth specs: left out of the API reference,
/// like the Discord sign-in's.
/// </para>
/// </remarks>
public static class BlueskyOAuthEndpoints
{
    public const string SettingsPage = "/settings";

    /// <summary>The query value Settings → Bluesky reads after Bluesky sends the browser back.</summary>
    public const string ResultParameter = "bluesky";

    public static IEndpointRouteBuilder MapBlueskyOAuth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(BlueskyOAuth.MetadataPath, async (
                [FromServices] ModbotContext db,
                [FromServices] BlueskyOAuth oauth,
                CancellationToken ct) =>
            {
                var publicAddress = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => s.PublicAddress)
                    .FirstOrDefaultAsync(ct);

                if (!BlueskyOAuth.Offered(publicAddress))
                    return Results.NotFound();

                var key = await oauth.ClientKeyAsync(db, ct);

                return BlueskyOAuth.Metadata(publicAddress, key) is { } document
                    ? Results.Text(document.ToJsonString(), "application/json")
                    : Results.NotFound();
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet(BlueskyOAuth.CallbackPath, async (
                [FromQuery] string? code,
                [FromQuery] string? state,
                [FromQuery] string? iss,
                [FromQuery] string? error,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] BlueskyOAuth oauth,
                [FromServices] BlueskySession session,
                [FromServices] BlueskyClient client,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                // Only a person who may change settings finishes a sign-in; anyone else is not the
                // person who started it.
                var person = ModbotAuth.UserIdOf(http.User);
                var held = ModbotAuth.PermissionsOf(http.User);
                var may = person is not null
                    && (held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.ManageSettings));

                var finish = await oauth.FinishAsync(db, code, state, iss, error, may ? person : null, ct);

                switch (finish.End)
                {
                    case BlueskyOAuthEnd.SignedIn:
                        break;
                    case BlueskyOAuthEnd.Limited:
                        await session.StopAsync(db, finish.Failure!, ct);
                        return Results.Redirect(Back("limited"));
                    default:
                        return Results.Redirect(Back(Word(finish.End)));
                }

                var tokens = finish.Tokens!;
                var pending = finish.Pending!;
                var server = new Uri(pending.Server);

                var settings = await db.GetSettingsAsync(ct);
                var handleBefore = settings.BlueskyHandle;
                var hadPassword = settings.BlueskyAppPasswordEncrypted is not null;
                var oauthBefore = settings.BlueskyOAuthSignedIn;

                await session.SignedInWithBlueskyAsync(db, tokens, pending.Handle, server, ct);

                // What Check reads, read now: the display name and whether the account is marked as
                // automated. A profile that could not be read leaves both unknown, not the sign-in failed.
                var profile = await client.ProfileAsync(server, tokens.Did, ct);
                if (profile.Failure is { StopsTheLane: true } limited)
                    await session.StopAsync(db, limited, ct);

                await db.Entry(settings).ReloadAsync(ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.BlueskyCheckedAt = clock.UtcNow;
                settings.BlueskyDisplayName = profile.Value?.DisplayName;
                settings.BlueskyAutomated = profile.Value?.Automated ?? false;
                settings.BlueskyProblem = null;

                var change = new SettingsChange("bluesky")
                    .Field("blueskyHandle", handleBefore, pending.Handle)
                    .Secret("blueskyAppPassword", hadPassword)
                    .Field("blueskySignedInWithBluesky", oauthBefore, true)
                    .Secret("blueskySignIn", true);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Redirect(Back("signed-in"));
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>Settings → Bluesky, with what happened for it to say.</summary>
    public static string Back(string result) =>
        $"{SettingsPage}?{ResultParameter}={Uri.EscapeDataString(result)}#bluesky";

    /// <summary>The word the page reads for how a sign-in ended.</summary>
    public static string Word(BlueskyOAuthEnd end) => end switch
    {
        BlueskyOAuthEnd.SignedIn => "signed-in",
        BlueskyOAuthEnd.Cancelled => "cancelled",
        BlueskyOAuthEnd.Expired => "expired",
        BlueskyOAuthEnd.OtherPerson => "signed-out",
        BlueskyOAuthEnd.WrongAccount => "wrong-account",
        BlueskyOAuthEnd.Unreachable => "unreachable",
        BlueskyOAuthEnd.Limited => "limited",
        _ => "refused",
    };
}
