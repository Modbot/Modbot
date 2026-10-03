using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Bluesky, as stored. The app password and the session are never returned.</summary>
/// <param name="Handle">The account's handle, without the <c>@</c>.</param>
/// <param name="AppPasswordStored">Whether an app password is saved.</param>
/// <param name="Posting">Whether posts go to Bluesky. Off until turned on after a good Check.</param>
/// <param name="CanPost">The last Check passed and the app password has not been refused since: Posting may be turned on.</param>
/// <param name="Check">What the last Check found. Null when it has not run since the handle or app password changed.</param>
/// <param name="LimitedUntil">Bluesky is limiting Modbot: nothing is sent to Bluesky, Check included, before this. Null once it has passed.</param>
/// <param name="SignInAfter">Modbot's own sign-in guard held the last Check back: no sign-in before this. Null otherwise.</param>
public sealed record BlueskySettingsView(
    string? Handle,
    bool AppPasswordStored,
    bool Posting,
    bool CanPost,
    BlueskyCheckView? Check,
    DateTimeOffset? LimitedUntil,
    DateTimeOffset? SignInAfter);

/// <summary>What the last Check found.</summary>
/// <param name="Handle">The handle the account answered to.</param>
/// <param name="DisplayName">The account's display name, or null for none.</param>
/// <param name="Automated">The account is marked as automated (decision 10).</param>
/// <param name="Problem">What went wrong, in a sentence. Null when Check passed.</param>
public sealed record BlueskyCheckView(DateTimeOffset At, string? Handle, string? DisplayName, bool Automated, string? Problem);

/// <param name="Handle">The handle, with or without the <c>@</c>. Null or empty keeps the stored one.</param>
/// <param name="AppPassword">
/// An app password, made in Bluesky's settings (four groups of four letters or digits). Null or
/// empty keeps the stored one. Anything else is refused with "Use an app password.".
/// </param>
/// <param name="Posting">Turn posting to Bluesky on or off. Null keeps it. On needs a Check that passed.</param>
public sealed record BlueskySettingsUpdate(string? Handle = null, string? AppPassword = null, bool? Posting = null);

/// <summary>
/// Settings → Bluesky (Bluesky design §3.1, posts design §4.2c): the handle, the app password, Check,
/// Posting and Remove.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only an app password is taken.</strong> A value not shaped like one is refused with "Use an
/// app password.", so the account's main password, which can change its email and password, is never
/// kept. The app password is encrypted with <see cref="ISecretProtector"/>, never returned, and
/// audited as a secret (changed, never what to).
/// </para>
/// <para>
/// <strong>Check never posts and never writes to the account.</strong> It finds the account from the
/// handle (both ways), uses the saved session if it still works (<c>getSession</c>) and otherwise
/// signs in once, within the sign-in guard, and reads the profile record for the display name and
/// whether the account is marked as automated. It answers 200 with what it found, a refusal included,
/// as the Google Calendar Check does.
/// </para>
/// <para>
/// A rate limit from Bluesky stops every call to it until it resets, Check included (CLAUDE.md:
/// never retry a 429). A Check pressed before then sends nothing.
/// </para>
/// </remarks>
public static class BlueskySettingsEndpoints
{
    public const string NotAHandle = "This is not a Bluesky handle.";
    public const string CheckFirst = "Check the account first.";

    public static IEndpointRouteBuilder MapBlueskySettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/bluesky")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(View(await db.GetSettingsAsync(ct), clock.UtcNow)))
            .WithName("GetBlueskySettings")
            .WithSummary("Get Bluesky settings")
            .WithDescription(
                "The Bluesky account Modbot posts as: its handle, whether an app password is saved, "
                + "Posting, and what the last Check found. The app password and the session are never returned.")
            .Produces<BlueskySettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] BlueskySettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                string? handle = null;
                if (!string.IsNullOrWhiteSpace(body.Handle))
                {
                    handle = BlueskyIdentity.NormaliseHandle(body.Handle);
                    if (handle is null)
                        return Results.BadRequest(new { error = NotAHandle });
                }

                string? password = null;
                if (!string.IsNullOrWhiteSpace(body.AppPassword))
                {
                    if (!BlueskyText.IsAppPassword(body.AppPassword))
                        return Results.BadRequest(new { error = BlueskyErrors.UseAnAppPassword });

                    // Kept exactly as typed, but for the spaces around it: a secret is never rewritten.
                    password = body.AppPassword.Trim();
                }

                var settings = await db.GetSettingsAsync(ct);
                var handleBefore = settings.BlueskyHandle;
                var passwordBefore = settings.BlueskyAppPasswordEncrypted;
                var postingBefore = settings.BlueskyPostingOn;

                var handleChanged = handle is not null && !string.Equals(handle, handleBefore, StringComparison.Ordinal);
                var passwordChanged = password is not null && password != StoredPassword(protector, passwordBefore);
                var postingAfter = body.Posting ?? postingBefore;

                // Posting goes on only over a Check that passed for this very handle and app password.
                if (postingAfter && !postingBefore && (handleChanged || passwordChanged || !PostSites.BlueskyReady(settings)))
                    return Results.BadRequest(new { error = CheckFirst });

                if (!handleChanged && !passwordChanged && postingAfter == postingBefore)
                    return Results.Ok(View(settings, clock.UtcNow));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (handleChanged)
                {
                    settings.BlueskyHandle = handle;

                    // Another handle may be another account: what was found for the old one goes.
                    settings.BlueskyDid = null;
                    settings.BlueskyServer = null;
                    settings.BlueskySessionEncrypted = null;
                }

                if (passwordChanged)
                {
                    settings.BlueskyAppPasswordEncrypted = protector.Protect(password!);

                    // A new app password may be tried at once; the day's count stays. A session made
                    // with the old one goes with it.
                    settings.BlueskySessionEncrypted = null;
                    settings.BlueskySignInRefused = false;
                    settings.BlueskySignedInAt = null;
                }

                // What Check found was about the handle and app password it was found with.
                if (handleChanged || passwordChanged)
                    ClearCheck(settings);

                settings.BlueskyPostingOn = postingAfter;

                var change = new SettingsChange("bluesky")
                    .Field("blueskyHandle", handleBefore, settings.BlueskyHandle)
                    .Secret("blueskyAppPassword", passwordChanged)
                    .Field("blueskyPosting", postingBefore, postingAfter);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("SetBlueskySettings")
            .WithSummary("Update Bluesky settings")
            .WithDescription(
                "Save the handle, an app password, Posting, or any of them. Only an app password is "
                + "taken (four groups of four letters or digits, made in Bluesky's settings); anything "
                + "else is refused with \"Use an app password.\". It is kept encrypted and never returned. "
                + "Saving the handle or the app password clears what the last Check found, and posts wait "
                + "until Check passes again. Posting on needs a Check that passed.")
            .Produces<BlueskySettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("bluesky")
                    .Field("blueskyHandle", settings.BlueskyHandle, null)
                    .Secret("blueskyAppPassword", settings.BlueskyAppPasswordEncrypted is not null)
                    .Field("blueskyPosting", settings.BlueskyPostingOn, false);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.BlueskyHandle = null;
                settings.BlueskyDid = null;
                settings.BlueskyServer = null;
                settings.BlueskyAppPasswordEncrypted = null;
                settings.BlueskySessionEncrypted = null;
                settings.BlueskySignInRefused = false;
                settings.BlueskyPostingOn = false;
                ClearCheck(settings);

                // The stop and the day's sign-in count are kept: both are Bluesky's limits on this
                // server, and an account put back at once must not cut them short.
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("RemoveBlueskySettings")
            .WithSummary("Remove the Bluesky account")
            .WithDescription(
                "Forget the handle, the app password, the session and what Check found, and turn Posting "
                + "off. Nothing on Bluesky is changed: posts already sent stay.")
            .Produces<BlueskySettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/check", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] BlueskyIdentity identity,
                [FromServices] BlueskySession session,
                [FromServices] BlueskyClient client,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (settings.BlueskyHandle is not { } handle)
                    return Results.BadRequest(new { error = "Enter the handle first." });

                if (settings.BlueskyAppPasswordEncrypted is null)
                    return Results.BadRequest(new { error = "Enter the app password first." });

                var now = clock.UtcNow;

                // Cold stop: nothing goes to Bluesky, and nothing is written, while Bluesky limits Modbot.
                if (settings.BlueskyStoppedUntil is { } until && now < until)
                    return Results.Ok(View(settings, now));

                var before = (settings.BlueskyDisplayName, settings.BlueskyAutomated, settings.BlueskyProblem);
                var found = await CheckAsync(db, settings, handle, identity, session, client, ct);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // The session may have written its own columns meanwhile (new tokens, a refused app
                // password, a stop); this save writes only what Check found.
                settings.BlueskyCheckedAt = now;
                settings.BlueskyDisplayName = found.DisplayName;
                settings.BlueskyAutomated = found.Automated;
                settings.BlueskyProblem = found.Problem;

                var change = new SettingsChange("bluesky")
                    .Field("checkedDisplayName", before.BlueskyDisplayName, found.DisplayName)
                    .Field("checkedAutomated", before.BlueskyAutomated, found.Automated)
                    .Field("checkedProblem", before.BlueskyProblem, found.Problem);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                await db.Entry(settings).ReloadAsync(ct);
                return Results.Ok(View(settings, now));
            })
            .WithName("CheckBluesky")
            .WithSummary("Check the Bluesky account")
            .WithDescription(
                "Find the account from the handle, sign in (the saved session when it still works, "
                + "otherwise the app password, at most once every 10 minutes and 20 times a day), and "
                + "read the account's display name and whether it is marked as automated. Never posts "
                + "and writes nothing to the account. Answers 200 with what it found, a refusal included. "
                + "While Bluesky is limiting Modbot nothing is sent and the stored result is returned.")
            .Produces<BlueskySettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>What one Check found, before it is written.</summary>
    private sealed record Found(string? DisplayName = null, bool Automated = false, string? Problem = null);

    private static async Task<Found> CheckAsync(
        ModbotContext db,
        Core.Data.Entities.Settings settings,
        string handle,
        BlueskyIdentity identity,
        BlueskySession session,
        BlueskyClient client,
        CancellationToken ct)
    {
        var account = await identity.FindAsync(handle, ct);
        if (account.Value is not { } found)
            return new Found(Problem: account.Failure is { Unclear: true } ? BlueskyErrors.Unreachable : BlueskyErrors.HandleNotFound);

        // The handle may now lead to another account, or the account to another server. A session
        // made for another account is not used.
        if (!string.Equals(found.Did, settings.BlueskyDid, StringComparison.Ordinal))
            settings.BlueskySessionEncrypted = null;

        settings.BlueskyDid = found.Did;
        settings.BlueskyServer = found.Server.ToString();
        settings.BlueskyHandle = found.Handle;
        await db.SaveChangesAsync(ct);

        var signIn = await session.AccessAsync(db, prove: true, ct);
        if (signIn.Access is not { } access)
        {
            return new Found(Problem: signIn.Problem);
        }

        var profile = await client.ProfileAsync(access.Server, access.Did, ct);

        if (profile.Failure is { StopsTheLane: true } limited)
        {
            await session.StopAsync(db, limited, ct);
            return new Found(Problem: BlueskyErrors.Limited);
        }

        // Signed in: a profile that could not be read leaves the name and the label unknown, not the
        // account failed.
        return new Found(profile.Value?.DisplayName, profile.Value?.Automated ?? false);
    }

    /// <summary>
    /// The stored app password, or null when there is none or it cannot be read (the database's key
    /// changed, say): then any app password saved counts as new, and the save goes through rather
    /// than failing, the way <see cref="BlueskySession"/> reads it.
    /// </summary>
    private static string? StoredPassword(ISecretProtector protector, string? encrypted)
    {
        try
        {
            return protector.Unprotect(encrypted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static void ClearCheck(Core.Data.Entities.Settings settings)
    {
        settings.BlueskyCheckedAt = null;
        settings.BlueskyDisplayName = null;
        settings.BlueskyAutomated = false;
        settings.BlueskyProblem = null;
    }

    private static BlueskySettingsView View(Core.Data.Entities.Settings settings, DateTimeOffset now)
    {
        // Said only while the guard is why the last Check failed; a session that works needs no sign-in.
        var signInAfter = settings.BlueskyProblem == BlueskyErrors.TooManySignIns ? BlueskySession.SignInAllowedAt(settings, now) : null;

        var check = settings.BlueskyCheckedAt is { } at
            ? new BlueskyCheckView(at, settings.BlueskyHandle, settings.BlueskyDisplayName, settings.BlueskyAutomated, settings.BlueskyProblem)
            : null;

        return new BlueskySettingsView(
            settings.BlueskyHandle,
            settings.BlueskyAppPasswordEncrypted is not null,
            settings.BlueskyPostingOn,
            PostSites.BlueskyReady(settings),
            check,
            settings.BlueskyStoppedUntil is { } until && now < until ? until : null,
            signInAfter);
    }
}
