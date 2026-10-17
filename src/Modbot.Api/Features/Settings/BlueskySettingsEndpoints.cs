using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.Api.Auth;
using Modbot.Api.Features.Calendar;
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
/// <param name="SignInWithBluesky">
/// Signing in with Bluesky's own sign-in page is offered: the public address is https with no port.
/// </param>
/// <param name="SignedInWithBluesky">The account was signed in with Bluesky's own sign-in page, not an app password.</param>
/// <param name="PostTags">The tags every calendar post ends with after its categories' tags: <c>#VRChat #Karaoke</c>. Empty for none.</param>
/// <param name="PinNextEvent">Modbot keeps the next calendar date's post pinned on the account's profile.</param>
/// <param name="LineupOn">Modbot posts the week's line-up once a week.</param>
/// <param name="LineupDay">The day of the week it is posted, 0 (Sunday) to 6.</param>
/// <param name="LineupHour">The hour of that day, 0 to 23, in <paramref name="LineupTimeZone"/>.</param>
/// <param name="LineupTimeZone">The IANA zone the day and hour are in, and the times in the post. Null is UTC.</param>
/// <param name="KlipyKey">The Klipy key the calendar form's GIF button searches with. Empty for none.</param>
public sealed record BlueskySettingsView(
    string? Handle,
    bool AppPasswordStored,
    bool Posting,
    bool CanPost,
    BlueskyCheckView? Check,
    DateTimeOffset? LimitedUntil,
    DateTimeOffset? SignInAfter,
    bool SignInWithBluesky = false,
    bool SignedInWithBluesky = false,
    string PostTags = "",
    bool PinNextEvent = false,
    bool LineupOn = false,
    int LineupDay = 1,
    int LineupHour = 12,
    string? LineupTimeZone = null,
    string KlipyKey = "");

/// <param name="Handle">The handle to sign in as, with or without the <c>@</c>. Null or empty uses the stored one.</param>
public sealed record BlueskySignInRequest(string? Handle = null);

/// <summary>Where to send the browser to sign in with Bluesky.</summary>
/// <param name="Url">Bluesky's sign-in page for this request. It sends the browser back to Modbot.</param>
public sealed record BlueskySignInStart(string Url);

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
/// <param name="PinNextEvent">Turn pinning the next calendar date's post on the profile on or off. Null keeps it.</param>
/// <param name="LineupOn">Turn the weekly line-up on or off. Null keeps it.</param>
/// <param name="LineupDay">The day of the week the line-up is posted, 0 (Sunday) to 6. Null keeps it.</param>
/// <param name="LineupHour">The hour of that day, 0 to 23, in <paramref name="LineupTimeZone"/>. Null keeps it.</param>
/// <param name="LineupTimeZone">An IANA zone name; empty for UTC. Null keeps it.</param>
/// <param name="KlipyKey">The Klipy key the calendar form's GIF button searches with: letters, numbers, <c>-</c> and <c>_</c>. Empty removes it; null keeps it.</param>
public sealed record BlueskySettingsUpdate(
    string? Handle = null, string? AppPassword = null, bool? Posting = null, string? PostTags = null, bool? PinNextEvent = null,
    bool? LineupOn = null, int? LineupDay = null, int? LineupHour = null, string? LineupTimeZone = null, string? KlipyKey = null);

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
/// <para>
/// <strong>Sign in with Bluesky</strong> (step 3b), for an install whose public address is https
/// with no port: <c>POST /sign-in</c> finds the account from the handle both ways and answers the
/// address of Bluesky's sign-in page; <see cref="BlueskyOAuthEndpoints"/> takes the browser back.
/// One way of signing in at a time: saving an app password ends a sign-in with Bluesky, and signing
/// in with Bluesky forgets the app password.
/// </para>
/// </remarks>
public static class BlueskySettingsEndpoints
{
    public const string NotAHandle = "This is not a Bluesky handle.";
    public const string CheckFirst = "Check the account first.";
    public const string LineupDayRange = "The day must be between Sunday and Saturday.";
    public const string LineupHourRange = "The hour must be between 0 and 23.";
    public const string LineupZoneUnknown = "That time zone is not known.";
    private static readonly System.Text.RegularExpressions.Regex KlipyKeyPattern =
        new("^[A-Za-z0-9_-]{1,200}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public const string KlipyKeyShape = "The Klipy key can only have letters, numbers, - and _.";
    public const string NeedsPublicAddress = "Bluesky sign-in needs a public https address.";

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

                string? postTags = null;
                if (body.PostTags is not null)
                {
                    if (BlueskyTags.ProblemWith(body.PostTags) is { } tagsProblem)
                        return Results.BadRequest(new { error = tagsProblem });

                    postTags = BlueskyTags.Write(BlueskyTags.ParseList(body.PostTags));
                }

                if (body.LineupDay is < 0 or > 6)
                    return Results.BadRequest(new { error = LineupDayRange });

                if (body.LineupHour is < 0 or > 23)
                    return Results.BadRequest(new { error = LineupHourRange });

                var lineupZone = body.LineupTimeZone?.Trim();
                if (!string.IsNullOrEmpty(lineupZone) && InsightTimes.FindTimeZone(lineupZone) is null)
                    return Results.BadRequest(new { error = LineupZoneUnknown });

                var klipyKey = body.KlipyKey?.Trim();
                if (!string.IsNullOrEmpty(klipyKey) && !KlipyKeyPattern.IsMatch(klipyKey))
                    return Results.BadRequest(new { error = KlipyKeyShape });

                var settings = await db.GetSettingsAsync(ct);
                var handleBefore = settings.BlueskyHandle;
                var passwordBefore = settings.BlueskyAppPasswordEncrypted;
                var postingBefore = settings.BlueskyPostingOn;
                var oauthBefore = settings.BlueskyOAuthSignedIn;

                var handleChanged = handle is not null && !string.Equals(handle, handleBefore, StringComparison.Ordinal);
                var passwordChanged = password is not null && password != StoredPassword(protector, passwordBefore);
                var postingAfter = body.Posting ?? postingBefore;
                var tagsBefore = settings.BlueskyPostTags ?? string.Empty;
                var tagsChanged = postTags is not null && postTags != tagsBefore;
                var pinBefore = settings.BlueskyPinNextEvent;
                var pinAfter = body.PinNextEvent ?? pinBefore;
                var lineupOnBefore = settings.BlueskyLineupOn;
                var lineupDayBefore = settings.BlueskyLineupDay;
                var lineupHourBefore = settings.BlueskyLineupHour;
                var lineupZoneBefore = settings.BlueskyLineupTimeZone;
                var lineupOnAfter = body.LineupOn ?? lineupOnBefore;
                var lineupDayAfter = body.LineupDay ?? settings.BlueskyLineupDay;
                var lineupHourAfter = body.LineupHour ?? settings.BlueskyLineupHour;
                var lineupZoneAfter = body.LineupTimeZone is null
                    ? settings.BlueskyLineupTimeZone
                    : (lineupZone is { Length: > 0 } ? lineupZone : null);
                var klipyBefore = settings.KlipyKey;
                var klipyAfter = klipyKey is null ? klipyBefore : (klipyKey.Length == 0 ? null : klipyKey);
                var klipyChanged = !string.Equals(klipyAfter, klipyBefore, StringComparison.Ordinal);
                var lineupChanged = lineupOnAfter != lineupOnBefore
                    || lineupDayAfter != lineupDayBefore
                    || lineupHourAfter != lineupHourBefore
                    || !string.Equals(lineupZoneAfter, lineupZoneBefore, StringComparison.Ordinal);

                // Posting goes on only over a Check that passed for this very handle and app password.
                if (postingAfter && !postingBefore && (handleChanged || passwordChanged || !PostSites.BlueskyReady(settings)))
                    return Results.BadRequest(new { error = CheckFirst });

                if (!handleChanged && !passwordChanged && !tagsChanged && postingAfter == postingBefore && pinAfter == pinBefore && !lineupChanged && !klipyChanged)
                    return Results.Ok(View(settings, clock.UtcNow));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (handleChanged)
                {
                    settings.BlueskyHandle = handle;

                    // Another handle may be another account: what was found for the old one goes,
                    // a sign-in with Bluesky included.
                    settings.BlueskyDid = null;
                    settings.BlueskyServer = null;
                    settings.BlueskySessionEncrypted = null;
                    settings.BlueskyOAuthSignedIn = false;

                    // The pin was on the old account's profile.
                    settings.BlueskyPinnedUri = null;
                    settings.BlueskyPinCheckedAt = null;
                    settings.BlueskyPinError = null;
                    settings.BlueskyPinErrorAt = null;

                    // A profile save that failed was on the old account.
                    settings.BlueskyProfileError = null;
                    settings.BlueskyProfileErrorAt = null;

                    // Replies are read from the new account's start, not from where the old one got to.
                    ForgetReplies(settings);
                }

                if (passwordChanged)
                {
                    settings.BlueskyAppPasswordEncrypted = protector.Protect(password!);

                    // A new app password may be tried at once; the day's count stays. A session made
                    // with the old one goes with it, and so does a sign-in with Bluesky: one way of
                    // signing in at a time.
                    settings.BlueskySessionEncrypted = null;
                    settings.BlueskySignInRefused = false;
                    settings.BlueskySignedInAt = null;
                    settings.BlueskyOAuthSignedIn = false;
                }

                // What Check found was about the handle and app password it was found with.
                if (handleChanged || passwordChanged)
                    ClearCheck(settings);

                settings.BlueskyPostingOn = postingAfter;

                if (tagsChanged)
                    settings.BlueskyPostTags = postTags!.Length == 0 ? null : postTags;

                if (pinAfter != pinBefore)
                {
                    settings.BlueskyPinNextEvent = pinAfter;

                    // Turned on, the profile is looked at on the next pass. An old failure is no
                    // news about the new setting.
                    settings.BlueskyPinCheckedAt = null;
                    settings.BlueskyPinError = null;
                    settings.BlueskyPinErrorAt = null;
                }

                if (lineupChanged)
                {
                    settings.BlueskyLineupOn = lineupOnAfter;
                    settings.BlueskyLineupDay = lineupDayAfter;
                    settings.BlueskyLineupHour = lineupHourAfter;
                    settings.BlueskyLineupTimeZone = lineupZoneAfter;

                    // A time that went by before this was turned on, or moved, never makes a post
                    // straight away: the first one is the next time the settings name.
                    if (lineupOnAfter)
                        settings.BlueskyLineupHandledThrough = CalendarBlueskyLineupPass.LatestMoment(settings, clock.UtcNow);
                }

                if (klipyChanged)
                    settings.KlipyKey = klipyAfter;

                var change = new SettingsChange("bluesky")
                    .Field("blueskyHandle", handleBefore, settings.BlueskyHandle)
                    .Secret("blueskyAppPassword", passwordChanged)
                    .Field("blueskySignedInWithBluesky", oauthBefore, settings.BlueskyOAuthSignedIn)
                    .Field("blueskyPosting", postingBefore, postingAfter)
                    .Field("blueskyPostTags", tagsBefore, settings.BlueskyPostTags ?? string.Empty)
                    .Field("blueskyPinNextEvent", pinBefore, pinAfter)
                    .Field("blueskyLineupOn", lineupOnBefore, lineupOnAfter)
                    .Field("blueskyLineupDay", lineupDayBefore, lineupDayAfter)
                    .Field("blueskyLineupHour", lineupHourBefore, lineupHourAfter)
                    .Field("blueskyLineupTimeZone", lineupZoneBefore, lineupZoneAfter)
                    .Secret("klipyKey", klipyChanged);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("SetBlueskySettings")
            .WithSummary("Update Bluesky settings")
            .WithDescription(
                "Save the handle, an app password, Posting, the tags every calendar post ends with (PostTags, "
                + "at most 5, each at most 64 characters; empty clears them), PinNextEvent (keep the next calendar date's post pinned on the profile), "
                + "LineupOn, LineupDay (0 is Sunday), LineupHour (0 to 23) and LineupTimeZone (an IANA name, empty for UTC): once a week the next seven days' public dates go out as one post, or any of them. Only an app password is "
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
                    .Field("blueskySignedInWithBluesky", settings.BlueskyOAuthSignedIn, false)
                    .Field("blueskyPosting", settings.BlueskyPostingOn, false)
                    .Field("blueskyPinNextEvent", settings.BlueskyPinNextEvent, false);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.BlueskyHandle = null;
                settings.BlueskyDid = null;
                settings.BlueskyServer = null;
                settings.BlueskyAppPasswordEncrypted = null;
                settings.BlueskySessionEncrypted = null;
                settings.BlueskyOAuthSignedIn = false;
                settings.BlueskyOAuthPendingEncrypted = null;
                settings.BlueskySignInRefused = false;
                settings.BlueskyPostingOn = false;

                // The pin was on the removed account's profile.
                settings.BlueskyPinNextEvent = false;
                settings.BlueskyPinnedUri = null;
                settings.BlueskyPinCheckedAt = null;
                settings.BlueskyPinError = null;
                settings.BlueskyPinErrorAt = null;
                settings.BlueskyProfileError = null;
                settings.BlueskyProfileErrorAt = null;
                ForgetReplies(settings);
                ClearCheck(settings);

                // The stop and the day's sign-in count are kept: both are Bluesky's limits on this
                // server, and an account put back at once must not cut them short. Modbot's own
                // signing key is kept too: it is this server's, not the account's.
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("RemoveBlueskySettings")
            .WithSummary("Remove the Bluesky account")
            .WithDescription(
                "Forget the handle, the app password or the sign-in with Bluesky, the session and what Check found, and turn Posting "
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

                if (settings.BlueskyAppPasswordEncrypted is null && !settings.BlueskyOAuthSignedIn)
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
                + "otherwise a renewal of the sign-in with Bluesky, or the app password, at most once "
                + "every 10 minutes and 20 times a day), and "
                + "read the account's display name and whether it is marked as automated. Never posts "
                + "and writes nothing to the account. Answers 200 with what it found, a refusal included. "
                + "While Bluesky is limiting Modbot nothing is sent and the stored result is returned.")
            .Produces<BlueskySettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/sign-in", async (
                HttpContext http,
                [FromBody] BlueskySignInRequest? body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] BlueskyIdentity identity,
                [FromServices] BlueskyOAuth oauth,
                [FromServices] BlueskySession session,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (settings.PublicAddress is not { } publicAddress || !BlueskyOAuth.Offered(publicAddress))
                    return Results.BadRequest(new { error = NeedsPublicAddress });

                var handle = settings.BlueskyHandle;
                if (!string.IsNullOrWhiteSpace(body?.Handle))
                {
                    handle = BlueskyIdentity.NormaliseHandle(body.Handle);
                    if (handle is null)
                        return Results.BadRequest(new { error = NotAHandle });
                }

                if (handle is null)
                    return Results.BadRequest(new { error = "Enter the handle first." });

                // The browser that comes back must be this person's: the callback checks it.
                if (ModbotAuth.UserIdOf(http.User) is not { } person)
                    return Results.BadRequest(new { error = "Sign in to Modbot first." });

                // Cold stop: nothing goes to Bluesky while Bluesky limits Modbot.
                if (settings.BlueskyStoppedUntil is { } until && clock.UtcNow < until)
                    return Results.BadRequest(new { error = BlueskyErrors.Limited });

                // The handle to its account and back, as Check does: the tokens must be for this DID.
                var account = await identity.FindAsync(handle, ct);
                if (account.Value is not { } found)
                    return Results.BadRequest(new { error = account.Failure is { Unclear: true } ? BlueskyErrors.Unreachable : BlueskyErrors.HandleNotFound });

                var started = await oauth.StartAsync(db, publicAddress, found, person, ct);

                if (started.Value is { } url)
                    return Results.Ok(new BlueskySignInStart(url.ToString()));

                var failure = started.Failure!;

                if (failure.StopsTheLane)
                {
                    await session.StopAsync(db, failure, ct);
                    return Results.BadRequest(new { error = BlueskyErrors.Limited });
                }

                return Results.BadRequest(new { error = failure.Unclear ? BlueskyErrors.Unreachable : BlueskyErrors.OwnWords(failure) });
            })
            .WithName("StartBlueskySignIn")
            .WithSummary("Start signing in with Bluesky")
            .WithDescription(
                "Only when the public address is https with no port. Finds the account from the handle "
                + "(the one given, or the stored one) both ways, asks the account's sign-in server for a "
                + "sign-in (PAR, PKCE, DPoP, with Modbot's own signing key) and answers the address of "
                + "Bluesky's sign-in page. Bluesky sends the browser back to /api/bluesky/callback, which "
                + "only the person who started may finish, and only for this account. Nothing is saved "
                + "but the waiting sign-in, good for ten minutes.")
            .Produces<BlueskySignInStart>()
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

    /// <summary>The reading of replies, mentions and quotes starts again for another account: from then, with no old error.</summary>
    private static void ForgetReplies(Core.Data.Entities.Settings settings)
    {
        settings.BlueskyRepliesReadAt = null;
        settings.BlueskyRepliesCheckedAt = null;
        settings.BlueskyRepliesError = null;
        settings.BlueskyRepliesErrorAt = null;
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
            signInAfter,
            BlueskyOAuth.Offered(settings.PublicAddress),
            settings.BlueskyOAuthSignedIn,
            settings.BlueskyPostTags ?? string.Empty,
            settings.BlueskyPinNextEvent,
            settings.BlueskyLineupOn,
            settings.BlueskyLineupDay,
            settings.BlueskyLineupHour,
            settings.BlueskyLineupTimeZone,
            settings.KlipyKey ?? string.Empty);
    }
}
