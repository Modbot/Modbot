using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Google Calendar, as stored. The private key is never returned.</summary>
/// <param name="KeyStored">Whether a key file is stored.</param>
/// <param name="Address">Modbot's Google address: the key's <c>client_email</c>, which the owner shares the calendar with.</param>
/// <param name="ProjectId">The Google Cloud project the key belongs to.</param>
/// <param name="CalendarId">The calendar Modbot uses.</param>
/// <param name="Check">What the last Check found. Null when it has not run since the key or the calendar changed.</param>
/// <param name="LimitedUntil">
/// Google is limiting Modbot: nothing is sent to Google, Check included, before this. Null once it has passed.
/// </param>
/// <param name="Links">Google's own links to the calendar, when Check found it public with every detail.</param>
/// <param name="Sending">Whether events are sent to the calendar (step 2). Off until turned on after a good Check.</param>
/// <param name="Removing">Every event Modbot put on the calendar is being deleted (Remove Modbot's events).</param>
/// <param name="CanSend">
/// The last Check passed and found Modbot may change events: Sending may be turned on, and Add all
/// and Remove Modbot's events may be used.
/// </param>
public sealed record GoogleCalendarSettingsView(
    bool KeyStored,
    string? Address,
    string? ProjectId,
    string? CalendarId,
    GoogleCalendarCheckView? Check,
    DateTimeOffset? LimitedUntil,
    GoogleCalendarLinksView? Links,
    bool Sending = false,
    bool Removing = false,
    bool CanSend = false);

/// <summary>What the last Check found.</summary>
/// <param name="CalendarName">The calendar's name on Google.</param>
/// <param name="TimeZone">The calendar's time zone.</param>
/// <param name="CanChangeEvents">Whether Modbot may make and change events on it.</param>
/// <param name="Public"><c>all</c>, <c>freeBusy</c>, <c>no</c>, or <c>unknown</c> when Google would not say.</param>
/// <param name="Problem">What went wrong, in a sentence. Null when Check passed.</param>
public sealed record GoogleCalendarCheckView(
    DateTimeOffset At,
    string? CalendarName,
    string? TimeZone,
    bool CanChangeEvents,
    string Public,
    string? Problem);

/// <summary>Google's links to a public calendar (Google Calendar design §3.9).</summary>
/// <param name="Subscribe">Adds the calendar in Google Calendar on a computer.</param>
/// <param name="PublicPage">The calendar's public page.</param>
/// <param name="ICal">The calendar's public iCal address, for any calendar app.</param>
public sealed record GoogleCalendarLinksView(string Subscribe, string PublicPage, string ICal);

/// <param name="KeyFile">The key file's text. Null or empty keeps the stored key.</param>
/// <param name="CalendarId">
/// The calendar id, or a public address, embed link or iCal address it can be read from. Null keeps
/// the stored one; empty removes it.
/// </param>
/// <param name="Sending">
/// Turn sending events to the calendar on or off. Null keeps it. On needs a Check that passed; off
/// leaves what is on Google as it is.
/// </param>
public sealed record GoogleCalendarSettingsUpdate(string? KeyFile, string? CalendarId, bool? Sending = null);

/// <param name="Added">How many events were ticked for Google Calendar.</param>
public sealed record GoogleCalendarAddAllView(int Added, GoogleCalendarSettingsView Settings);

/// <summary>
/// Settings → Google Calendar (Google Calendar design §3.1): the service account's key, the
/// calendar, Check, and (step 2) Sending, Add all and Remove Modbot's events.
/// </summary>
/// <remarks>
/// <para>
/// The key file is read here and only its private key, address, key id and project are kept; the
/// private key is encrypted with <see cref="ISecretProtector"/> and never returned. Every change is
/// audited, the key as a secret (changed, never what to).
/// </para>
/// <para>
/// Check writes nothing to Google: one token request and two reads, <c>events.list</c> for the
/// calendar's name, time zone and Modbot's role, and <c>acl.list</c> for whether it is public. What
/// it found is kept on the settings row and shown until the key or the calendar changes. It answers
/// 200 with what it found even when Google refused, like the AI and email tests: a refusal is a
/// finding, and Google's reason is the useful part.
/// </para>
/// <para>
/// A rate limit from Google stops every call to it until <see cref="Core.Data.Entities.Settings.GoogleStoppedUntil"/>,
/// Check included (CLAUDE.md: never retry a 429). A Check pressed before then sends nothing.
/// </para>
/// </remarks>
public static class GoogleCalendarSettingsEndpoints
{
    public static IEndpointRouteBuilder MapGoogleCalendarSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/google-calendar")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(View(await db.GetSettingsAsync(ct), clock.UtcNow)))
            .WithName("GetGoogleCalendarSettings")
            .WithSummary("Get Google Calendar settings")
            .WithDescription(
                "The Google Calendar connection: Modbot's Google address, the calendar, what the last "
                + "Check found, and the calendar's public links when it is public. The key is never returned.")
            .Produces<GoogleCalendarSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] GoogleSignIn signIn,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                // Read here rather than bound, so a body far past any key file is refused before it
                // is read whole (added 2026-10-03): Kestrel stops reading just past the limit.
                if (http.Request.ContentLength > MaxBodyBytes)
                    return Results.Json(new { error = "The key file is too big." }, statusCode: StatusCodes.Status413PayloadTooLarge);

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = MaxBodyBytes;

                GoogleCalendarSettingsUpdate? body;

                try
                {
                    body = await http.Request.ReadFromJsonAsync<GoogleCalendarSettingsUpdate>(ct);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException or InvalidOperationException)
                {
                    return ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
                        ? Results.Json(new { error = "The key file is too big." }, statusCode: StatusCodes.Status413PayloadTooLarge)
                        : Results.BadRequest(new { error = "This is not a settings body." });
                }

                if (body is null)
                    return Results.BadRequest(new { error = "This is not a settings body." });

                GoogleKeyFile? key = null;

                if (!string.IsNullOrWhiteSpace(body.KeyFile))
                {
                    key = GoogleKeyFile.Parse(body.KeyFile, out var keyError);
                    if (key is null)
                        return Results.BadRequest(new { error = keyError });
                }

                string? calendarId = null;

                if (body.CalendarId is not null && !string.IsNullOrWhiteSpace(body.CalendarId))
                {
                    calendarId = GoogleCalendarIds.Parse(body.CalendarId);
                    if (calendarId is null)
                        return Results.BadRequest(new { error = "This is not a calendar ID." });
                }

                var settings = await db.GetSettingsAsync(ct);
                var keyBefore = settings.GooglePrivateKeyEncrypted;
                var calendarBefore = settings.GoogleCalendarId;

                var keyChanged = key is not null
                    && !(key.ClientEmail == settings.GoogleClientEmail
                        && key.KeyId == settings.GoogleKeyId
                        && key.PrivateKeyPem == protector.Unprotect(settings.GooglePrivateKeyEncrypted));

                var calendarAfter = body.CalendarId is null ? calendarBefore : calendarId;
                var calendarChanged = !string.Equals(calendarBefore, calendarAfter, StringComparison.Ordinal);

                var sendingBefore = settings.GoogleSendingOn;
                var sendingAfter = body.Sending ?? sendingBefore;

                // Sending goes on only over a Check that passed for this very key and calendar.
                if (sendingAfter && !sendingBefore && (keyChanged || calendarChanged || !CalendarGoogle.SetUp(settings)))
                    return Results.BadRequest(new { error = "Check the calendar first." });

                if (!keyChanged && !calendarChanged && sendingAfter == sendingBefore)
                    return Results.Ok(View(settings, clock.UtcNow));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (keyChanged)
                {
                    settings.GoogleClientEmail = key!.ClientEmail;
                    settings.GoogleKeyId = key.KeyId;
                    settings.GoogleProjectId = key.ProjectId;
                    settings.GooglePrivateKeyEncrypted = protector.Protect(key.PrivateKeyPem);
                }

                settings.GoogleCalendarId = calendarAfter;

                // What Check found was about the key and calendar it was found with.
                if (keyChanged || calendarChanged)
                    ClearCheck(settings);

                // Sending on again ends a Remove Modbot's events still going: the events are wanted.
                settings.GoogleSendingOn = sendingAfter;
                if (sendingAfter)
                    settings.GoogleRemovingEvents = false;

                var change = new SettingsChange("googleCalendar")
                    .Secret("googleKey", !string.Equals(keyBefore, settings.GooglePrivateKeyEncrypted, StringComparison.Ordinal))
                    .Field("googleCalendarId", calendarBefore, settings.GoogleCalendarId)
                    .Field("googleSending", sendingBefore, sendingAfter);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                if (keyChanged)
                    signIn.Forget();

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("SetGoogleCalendarSettings")
            .WithSummary("Update Google Calendar settings")
            .WithDescription(
                "Save the Google service account's key file, the calendar ID, Sending, or any of them. "
                + "Only the key file's address, key id, project and private key are kept, the private "
                + "key encrypted. Saving the key or the calendar clears what the last Check found. "
                + "Sending on needs a Check that passed; off leaves the events on Google as they are. "
                + "A body over 64 KB is refused with 413.")
            .Accepts<GoogleCalendarSettingsUpdate>("application/json")
            .Produces<GoogleCalendarSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        group.MapDelete("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] GoogleSignIn signIn,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("googleCalendar")
                    .Secret("googleKey", settings.GooglePrivateKeyEncrypted is not null)
                    .Field("googleCalendarId", settings.GoogleCalendarId, null)
                    .Field("googleSending", settings.GoogleSendingOn, false);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.GoogleClientEmail = null;
                settings.GoogleKeyId = null;
                settings.GoogleProjectId = null;
                settings.GooglePrivateKeyEncrypted = null;
                settings.GoogleCalendarId = null;
                ClearCheck(settings);

                // A key put back later starts with Sending off, as a first one does. What is on
                // Google stays (design §3.6).
                settings.GoogleSendingOn = false;
                settings.GoogleRemovingEvents = false;

                // GoogleStoppedUntil is kept: a limit is Google's on the project, and a key put
                // back at once must not cut it short.
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                signIn.Forget();

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("ForgetGoogleCalendarSettings")
            .WithSummary("Forget Google Calendar settings")
            .WithDescription(
                "Remove the key, the calendar ID and what Check found, and turn Sending off. Nothing on "
                + "Google is changed.")
            .Produces<GoogleCalendarSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/check", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IModbotClock clock,
                [FromServices] GoogleSignIn signIn,
                [FromServices] GoogleCalendarClient google,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (settings.GooglePrivateKeyEncrypted is null || settings.GoogleClientEmail is null || settings.GoogleKeyId is null)
                    return Results.BadRequest(new { error = "Choose the key file first." });

                if (settings.GoogleCalendarId is not { } calendarId)
                    return Results.BadRequest(new { error = "Enter the calendar ID first." });

                var now = clock.UtcNow;

                // Cold stop: nothing goes to Google, and nothing is written, while Google limits Modbot.
                if (settings.GoogleStoppedUntil is { } until && now < until)
                    return Results.Ok(View(settings, now));

                var found = await CheckAsync(settings, calendarId, protector, signIn, google, ct);

                var change = new SettingsChange("googleCalendar")
                    .Field("checkedCalendarName", settings.GoogleCalendarName, found.Name)
                    .Field("checkedCanChangeEvents", settings.GoogleCanChange, found.CanChange)
                    .Field("checkedPublic", settings.GooglePublic, found.Public)
                    .Field("checkedProblem", settings.GoogleProblem, found.Problem);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.GoogleCheckedAt = now;
                settings.GoogleCalendarName = found.Name;
                settings.GoogleCalendarTimeZone = found.TimeZone;
                settings.GoogleCanChange = found.CanChange;
                settings.GooglePublic = found.Public;
                settings.GoogleProblem = found.Problem;

                if (found.Stop is { } stop)
                    settings.GoogleStoppedUntil = now + stop;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, now));
            })
            .WithName("CheckGoogleCalendar")
            .WithSummary("Check the Google Calendar connection")
            .WithDescription(
                "Sign in to Google with the stored key and read the calendar's name, time zone, whether "
                + "Modbot may change its events, and whether it is public. Writes nothing to Google. "
                + "Answers 200 with what it found, a refusal included. While Google is limiting Modbot "
                + "nothing is sent and the stored result is returned.")
            .Produces<GoogleCalendarSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        // Existing events: Add all (decision 6 A). Ticks every live event that may go; the sending
        // loop sends them. Written as one settings change with the count, not one entry per event.
        group.MapPost("/add-all", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (!CalendarGoogle.SetUp(settings))
                    return Results.BadRequest(new { error = "Check the calendar first." });

                var events = await db.CalendarEvents
                    .Where(e => e.DeletedAt == null
                        && !e.PublishToGoogle
                        && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
                    .ToListAsync(ct);

                var ticked = events.Where(e => !CalendarGoogle.MembersOnly(e)).ToList();

                foreach (var calendarEvent in ticked)
                    calendarEvent.PublishToGoogle = true;

                var change = new SettingsChange("googleCalendar").Field("eventsAdded", 0, ticked.Count);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(new GoogleCalendarAddAllView(ticked.Count, View(settings, clock.UtcNow)));
            })
            .WithName("AddAllEventsToGoogleCalendar")
            .WithSummary("Add every event to Google Calendar")
            .WithDescription(
                "Tick Google Calendar on every scheduled or open event that everyone may see. Events "
                + "only the group's members see are left as they are. They are sent once Sending is on. "
                + "Needs a Check that passed.")
            .Produces<GoogleCalendarAddAllView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        // Remove Modbot's events (decision 7 A): Sending goes off, and the loop deletes every event
        // Modbot put on the calendar, one call at a time.
        group.MapPost("/remove-events", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (!CalendarGoogle.SetUp(settings))
                    return Results.BadRequest(new { error = "Check the calendar first." });

                if (settings.GoogleRemovingEvents)
                    return Results.Ok(View(settings, clock.UtcNow));

                var change = new SettingsChange("googleCalendar")
                    .Field("googleSending", settings.GoogleSendingOn, false)
                    .Field("removingEvents", false, true);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.GoogleSendingOn = false;
                settings.GoogleRemovingEvents = true;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("RemoveModbotEventsFromGoogleCalendar")
            .WithSummary("Remove Modbot's events from Google Calendar")
            .WithDescription(
                "Turn Sending off and delete every event Modbot put on the Google calendar, through the "
                + "sending loop, a few each pass. Events the calendar's owner made are left alone. The "
                + "events stay ticked in Modbot. Needs a Check that passed.")
            .Produces<GoogleCalendarSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>The largest settings body read: a key file is about 2.3 KB, and refused over 16 KB.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>What one Check found, before it is written to the settings row.</summary>
    private sealed record Found(
        string? Name = null,
        string? TimeZone = null,
        bool CanChange = false,
        string? Public = null,
        string? Problem = null,
        TimeSpan? Stop = null);

    private static async Task<Found> CheckAsync(
        Core.Data.Entities.Settings settings,
        string calendarId,
        ISecretProtector protector,
        GoogleSignIn signIn,
        GoogleCalendarClient google,
        CancellationToken ct)
    {
        var pem = protector.Unprotect(settings.GooglePrivateKeyEncrypted);
        if (pem is null)
            return new Found(Problem: "Google did not accept the key.");

        var key = new GoogleCredentials(settings.GoogleClientEmail!, settings.GoogleKeyId!, pem);

        var token = await signIn.TokenAsync(key, fresh: true, ct);
        if (token.Value is not { } accessToken)
            return Refused(token.Failure!);

        var calendar = await google.CalendarAsync(accessToken, calendarId, ct);
        // A 403 for who Modbot is means it cannot see the calendar. Any other 403 that is not a limit
        // (the Calendar API not enabled in the project, say) is said in Google's own words: "can't
        // see this calendar" would send the operator to the calendar's sharing for nothing.
        if (calendar.Value is not { } info)
        {
            return calendar.Failure!.Problem == GoogleProblem.Forbidden
                ? new Found(Problem: GoogleErrors.IsNotAllowed(calendar.Failure) ? GoogleErrors.CannotSee : GoogleErrors.OwnWords(calendar.Failure))
                : Refused(calendar.Failure!);
        }

        // Asked even when Modbot may only read, so the Public line is right either way.
        var shared = await google.PublicAsync(accessToken, calendarId, ct);
        if (shared.Failure is { StopsTheLane: true } limited)
            return Refused(limited);

        var problem = info.CanChangeEvents ? null
            : info.CanRead ? GoogleErrors.ReadOnly
            : GoogleErrors.CannotSee;

        // A 403 or anything else on the sharing list leaves Public unknown (null), not a failure:
        // Modbot's role decides whether it can do its job, and the role came back.
        return new Found(info.Name, info.TimeZone, info.CanChangeEvents, shared.Value, problem);
    }

    private static Found Refused(GoogleFailure failure) => new(
        Problem: GoogleErrors.Sentence(failure),
        Stop: failure.StopsTheLane ? GoogleErrors.StopFor(failure) : null);

    private static void ClearCheck(Core.Data.Entities.Settings settings)
    {
        settings.GoogleCheckedAt = null;
        settings.GoogleCalendarName = null;
        settings.GoogleCalendarTimeZone = null;
        settings.GoogleCanChange = false;
        settings.GooglePublic = null;
        settings.GoogleProblem = null;
    }

    private static GoogleCalendarSettingsView View(Core.Data.Entities.Settings settings, DateTimeOffset now)
    {
        var check = settings.GoogleCheckedAt is { } at
            ? new GoogleCalendarCheckView(
                at,
                settings.GoogleCalendarName,
                settings.GoogleCalendarTimeZone,
                settings.GoogleCanChange,
                settings.GooglePublic ?? "unknown",
                settings.GoogleProblem)
            : null;

        var links = check is { Public: GooglePublic.All } && settings.GoogleCalendarId is { } id
            ? new GoogleCalendarLinksView(
                GoogleCalendarIds.SubscribeLink(id),
                GoogleCalendarIds.PublicPageLink(id, settings.GoogleCalendarTimeZone),
                GoogleCalendarIds.ICalLink(id))
            : null;

        return new GoogleCalendarSettingsView(
            settings.GooglePrivateKeyEncrypted is not null,
            settings.GoogleClientEmail,
            settings.GoogleProjectId,
            settings.GoogleCalendarId,
            check,
            settings.GoogleStoppedUntil is { } until && now < until ? until : null,
            links,
            settings.GoogleSendingOn,
            settings.GoogleRemovingEvents,
            CalendarGoogle.SetUp(settings));
    }
}
