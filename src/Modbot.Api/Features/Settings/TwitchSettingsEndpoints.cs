using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Twitch;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Core.Twitch;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Twitch, as stored. The client secret is never returned.</summary>
/// <param name="ClientId">The Twitch app's client id. Not secret.</param>
/// <param name="SecretStored">Whether a client secret is saved.</param>
/// <param name="Channel">The channel's login name.</param>
/// <param name="Check">What the last Check found. Null when it has not run since the client id, the secret or the channel changed.</param>
/// <param name="LimitedUntil">Twitch is limiting Modbot: nothing is sent to Twitch, Check included, before this. Null once it has passed.</param>
/// <param name="Live">Whether Modbot asks Twitch once a minute if the channel is live. Off until turned on after a good Check.</param>
/// <param name="CanGoLive">The last Check passed: Live may be turned on.</param>
/// <param name="PolledAt">When the poll last got an answer from Twitch.</param>
/// <param name="PollProblem">What went wrong at the last poll, or null.</param>
/// <param name="PostAfterMinutes">How many minutes the channel is live before a "live" post is made.</param>
/// <param name="PostEveryHours">No second "live" post comes within this many hours of the last.</param>
/// <param name="PostTitle">The "live" post's title, with <c>{title}</c>, <c>{category}</c> and <c>{link}</c>.</param>
/// <param name="PostText">The "live" post's text, with the same placeholders.</param>
/// <param name="Places">Where a "live" post goes: the sites ticked, with their own choices. Nothing is ticked to start.</param>
/// <param name="VRChatRoles">The VRChat group's roles as the last group read found them, for choosing who a VRChat post is for.</param>
public sealed record TwitchSettingsView(
    string? ClientId,
    bool SecretStored,
    string? Channel,
    TwitchCheckView? Check,
    DateTimeOffset? LimitedUntil,
    bool Live,
    bool CanGoLive,
    DateTimeOffset? PolledAt,
    string? PollProblem,
    int PostAfterMinutes,
    int PostEveryHours,
    string PostTitle,
    string PostText,
    TwitchPostPlaces Places,
    IReadOnlyList<PostRoleChoice> VRChatRoles);

/// <summary>What the last Check found.</summary>
/// <param name="ChannelName">The channel's display name on Twitch.</param>
/// <param name="Problem">What went wrong, in a sentence. Null when Check passed.</param>
public sealed record TwitchCheckView(DateTimeOffset At, string? ChannelName, string? Problem);

/// <param name="ClientId">The Twitch app's client id. Null keeps the stored one.</param>
/// <param name="ClientSecret">The Twitch app's client secret. Null or empty keeps the stored one.</param>
/// <param name="Channel">The channel's login, or a twitch.tv address it can be read from. Null keeps the stored one; empty removes it.</param>
/// <param name="Live">Turn the poll on or off. Null keeps it. On needs a Check that passed.</param>
/// <param name="PostAfterMinutes">Minutes live before a "live" post is made, 1 to 60. Null keeps it.</param>
/// <param name="PostEveryHours">Hours between two "live" posts, 1 to 168. Null keeps it.</param>
/// <param name="PostTitle">The post's title template. Null keeps it; empty goes back to the built-in one.</param>
/// <param name="PostText">The post's text template. Null keeps it; empty goes back to the built-in one.</param>
/// <param name="Places">Where a "live" post goes. Null keeps it. Each site ticked is checked as the Marketing tab checks a post.</param>
public sealed record TwitchSettingsUpdate(
    string? ClientId = null,
    string? ClientSecret = null,
    string? Channel = null,
    bool? Live = null,
    int? PostAfterMinutes = null,
    int? PostEveryHours = null,
    string? PostTitle = null,
    string? PostText = null,
    TwitchPostPlaces? Places = null);

/// <summary>
/// Settings → Twitch (Twitch design, step 1 and 2): the Twitch app's client id and secret, the
/// channel, Check, the poll's switch, and the "We're live on Twitch" post.
/// </summary>
/// <remarks>
/// <para>
/// The secret is encrypted with <see cref="ISecretProtector"/> and never returned. Every change is
/// audited, the secret as a secret (changed, never what to).
/// </para>
/// <para>
/// Check writes nothing to Twitch: one token request, Get Users for the channel's id and Get Streams
/// for whether the app token may ask. What it found is kept on the settings row and shown until the
/// client id, the secret or the channel changes. It answers 200 with what it found even when Twitch
/// refused, like the Google and AI tests: a refusal is a finding, and Twitch's reason is the useful
/// part.
/// </para>
/// <para>
/// A rate limit from Twitch stops every call to it until <see cref="Core.Data.Entities.Settings.TwitchStoppedUntil"/>,
/// Check included (CLAUDE.md: never retry a 429). A Check pressed before then sends nothing.
/// </para>
/// </remarks>
public static class TwitchSettingsEndpoints
{
    public const int MaxClientIdLength = 100;
    public const int MaxClientSecretLength = 200;
    public const int MaxTemplateTitleLength = Post.MaxTitleLength;
    public const int MaxTemplateTextLength = 2000;

    public static IEndpointRouteBuilder MapTwitchSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/twitch")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(View(await db.GetSettingsAsync(ct), clock.UtcNow)))
            .WithName("GetTwitchSettings")
            .WithSummary("Get Twitch settings")
            .WithDescription(
                "The Twitch connection: the app's client id, the channel, what the last Check found, "
                + "whether the poll is on, and the \"We're live on Twitch\" post: its template, "
                + "timings and where it goes. The client secret is never returned.")
            .Produces<TwitchSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] TwitchSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] TwitchSignIn signIn,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var problems = new List<string>();

                var clientId = body.ClientId?.Trim();
                if (clientId is not null && (clientId.Length > MaxClientIdLength || clientId.Any(char.IsWhiteSpace)))
                    problems.Add("This is not a client id.");

                var secret = string.IsNullOrWhiteSpace(body.ClientSecret) ? null : body.ClientSecret.Trim();
                if (secret is not null && (secret.Length > MaxClientSecretLength || secret.Any(char.IsWhiteSpace)))
                    problems.Add("This is not a client secret.");

                string? login = null;
                if (!string.IsNullOrWhiteSpace(body.Channel))
                {
                    login = TwitchRules.LoginOf(body.Channel);
                    if (login is null)
                        problems.Add("This is not a Twitch channel name.");
                }

                if (body.PostAfterMinutes is { } minutes
                    && minutes is < TwitchRules.LeastAfterMinutes or > TwitchRules.MostAfterMinutes)
                {
                    problems.Add($"Minutes live must be between {TwitchRules.LeastAfterMinutes} and {TwitchRules.MostAfterMinutes}.");
                }

                if (body.PostEveryHours is { } hours
                    && hours is < TwitchRules.LeastEveryHours or > TwitchRules.MostEveryHours)
                {
                    problems.Add($"Hours between posts must be between {TwitchRules.LeastEveryHours} and {TwitchRules.MostEveryHours}.");
                }

                var title = body.PostTitle is null ? null : PostTexts.TidyTitle(body.PostTitle);
                if (title is { Length: > MaxTemplateTitleLength })
                    problems.Add($"The title is longer than {MaxTemplateTitleLength} characters.");

                var text = body.PostText is null ? null : PostTexts.Tidy(body.PostText);
                if (text is { Length: > MaxTemplateTextLength })
                    problems.Add($"The text is longer than {MaxTemplateTextLength} characters.");

                var now = clock.UtcNow;

                // Each site ticked is checked the way the Marketing tab checks a post: the channel
                // is in the server, the role may be mentioned, the group and the account are set up.
                if (body.Places is { } places && problems.Count == 0)
                    problems.AddRange(await TwitchPosts.CheckPlacesAsync(db, places, now, ct));

                if (problems.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", problems), problems });

                var settings = await db.GetSettingsAsync(ct);

                var idBefore = settings.TwitchClientId;
                var secretBefore = settings.TwitchClientSecretEncrypted;
                var channelBefore = settings.TwitchChannelLogin;

                var idAfter = clientId is null ? idBefore : (clientId.Length == 0 ? null : clientId);
                var channelAfter = body.Channel is null ? channelBefore : login;

                var idChanged = !string.Equals(idBefore, idAfter, StringComparison.Ordinal);
                var secretChanged = secret is not null && secret != protector.Unprotect(settings.TwitchClientSecretEncrypted);
                var channelChanged = !string.Equals(channelBefore, channelAfter, StringComparison.Ordinal);

                var liveBefore = settings.TwitchLiveOn;
                var liveAfter = body.Live ?? liveBefore;

                // The poll goes on only over a Check that passed for this very app and channel.
                if (liveAfter && !liveBefore && (idChanged || secretChanged || channelChanged || !TwitchRules.SetUp(settings)))
                    return Results.BadRequest(new { error = "Check Twitch first." });

                var placesBefore = settings.TwitchPostPlaces;
                var placesAfter = body.Places is { } given ? given.Write() : placesBefore;

                var afterMinutes = body.PostAfterMinutes ?? settings.TwitchPostAfterMinutes;
                var everyHours = body.PostEveryHours ?? settings.TwitchPostEveryHours;
                var titleAfter = body.PostTitle is null ? settings.TwitchPostTitle : (title is { Length: > 0 } ? title : null);
                var textAfter = body.PostText is null ? settings.TwitchPostText : (text is { Length: > 0 } ? text : null);

                var change = new SettingsChange("twitch")
                    .Field("twitchClientId", idBefore, idAfter)
                    .Secret("twitchClientSecret", secretChanged)
                    .Field("twitchChannel", channelBefore, channelAfter)
                    .Field("twitchLive", liveBefore, liveAfter)
                    .Field("postAfterMinutes", settings.TwitchPostAfterMinutes, afterMinutes)
                    .Field("postEveryHours", settings.TwitchPostEveryHours, everyHours)
                    .Field("postTitle", settings.TwitchPostTitle, titleAfter)
                    .Field("postText", settings.TwitchPostText, textAfter)
                    .Document(placesBefore, placesAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings, now));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.TwitchClientId = idAfter;
                if (secretChanged)
                    settings.TwitchClientSecretEncrypted = protector.Protect(secret!);

                settings.TwitchChannelLogin = channelAfter;

                // What Check found was about the app and channel it was found with.
                if (idChanged || secretChanged || channelChanged)
                    ClearCheck(settings);

                settings.TwitchLiveOn = liveAfter;
                settings.TwitchPostAfterMinutes = afterMinutes;
                settings.TwitchPostEveryHours = everyHours;
                settings.TwitchPostTitle = titleAfter;
                settings.TwitchPostText = textAfter;
                settings.TwitchPostPlaces = placesAfter;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                if (idChanged || secretChanged)
                    signIn.Forget();

                return Results.Ok(View(settings, now));
            })
            .WithName("SetTwitchSettings")
            .WithSummary("Update Twitch settings")
            .WithDescription(
                "Save the Twitch app's client id and secret, the channel, whether the poll is on, or the "
                + "\"We're live on Twitch\" post's template, timings and sites, or any of them. The secret "
                + "is kept encrypted. Saving the app or the channel clears what the last Check found. "
                + "The poll on needs a Check that passed. A refusal (400) lists everything wrong at once, "
                + "in `problems`.")
            .Accepts<TwitchSettingsUpdate>("application/json")
            .Produces<TwitchSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] TwitchSignIn signIn,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("twitch")
                    .Field("twitchClientId", settings.TwitchClientId, null)
                    .Secret("twitchClientSecret", settings.TwitchClientSecretEncrypted is not null)
                    .Field("twitchChannel", settings.TwitchChannelLogin, null)
                    .Field("twitchLive", settings.TwitchLiveOn, false);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.TwitchClientId = null;
                settings.TwitchClientSecretEncrypted = null;
                settings.TwitchChannelLogin = null;
                ClearCheck(settings);

                // A secret put back later starts with the poll off, as a first one does.
                settings.TwitchLiveOn = false;
                settings.TwitchPolledAt = null;
                settings.TwitchPollProblem = null;

                // TwitchStoppedUntil is kept: a limit is Twitch's on the app, and a secret put back
                // at once must not cut it short. The post's template, timings and sites are kept too.
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                signIn.Forget();

                return Results.Ok(View(settings, clock.UtcNow));
            })
            .WithName("ForgetTwitchSettings")
            .WithSummary("Forget Twitch settings")
            .WithDescription(
                "Remove the client id, the secret, the channel and what Check found, and turn the poll "
                + "off. The post's template, timings and sites are kept.")
            .Produces<TwitchSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/check", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IModbotClock clock,
                [FromServices] TwitchSignIn signIn,
                [FromServices] TwitchClient twitch,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (string.IsNullOrWhiteSpace(settings.TwitchClientId) || settings.TwitchClientSecretEncrypted is null)
                    return Results.BadRequest(new { error = "Enter the client id and secret first." });

                if (settings.TwitchChannelLogin is not { } login)
                    return Results.BadRequest(new { error = "Enter the channel first." });

                var now = clock.UtcNow;

                // Cold stop: nothing goes to Twitch, and nothing is written, while Twitch limits Modbot.
                if (TwitchRules.Stopped(settings, now))
                    return Results.Ok(View(settings, now));

                var found = await CheckAsync(settings, login, protector, signIn, twitch, ct);

                var change = new SettingsChange("twitch")
                    .Field("checkedChannelName", settings.TwitchChannelName, found.Name)
                    .Field("checkedProblem", settings.TwitchProblem, found.Problem);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.TwitchCheckedAt = now;
                settings.TwitchChannelId = found.ChannelId;
                settings.TwitchChannelName = found.Name;
                settings.TwitchProblem = found.Problem;

                if (found.Failure is { IsALimit: true } stop)
                    settings.TwitchStoppedUntil = TwitchErrors.StopUntil(stop, now);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings, now));
            })
            .WithName("CheckTwitch")
            .WithSummary("Check the Twitch connection")
            .WithDescription(
                "Ask Twitch for an app token with the stored client id and secret, find the channel by "
                + "its login, and ask whether the token may read its stream. Writes nothing to Twitch. "
                + "Answers 200 with what it found, a refusal included. While Twitch is limiting Modbot "
                + "nothing is sent and the stored result is returned.")
            .Produces<TwitchSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>What one Check found, before it is written to the settings row.</summary>
    private sealed record Found(
        string? ChannelId = null,
        string? Name = null,
        string? Problem = null,
        TwitchFailure? Failure = null);

    private static async Task<Found> CheckAsync(
        Core.Data.Entities.Settings settings,
        string login,
        ISecretProtector protector,
        TwitchSignIn signIn,
        TwitchClient twitch,
        CancellationToken ct)
    {
        var clientId = settings.TwitchClientId!;
        var secret = protector.Unprotect(settings.TwitchClientSecretEncrypted);

        if (secret is null)
            return new Found(Problem: TwitchErrors.CredentialsNotAccepted);

        var token = await signIn.TokenAsync(clientId, secret, fresh: true, ct);

        if (token.Failure is { } refused)
            return Refused(refused);

        var accessToken = token.Value!;
        var channel = await twitch.ChannelAsync(accessToken, clientId, login, ct);

        if (channel.Failure is { } notFound)
        {
            if (notFound.Problem == TwitchProblem.Unauthorized)
                signIn.Refused(accessToken);

            // A login Twitch will not take at all (a 400) is said in Twitch's own words.
            return Refused(notFound);
        }

        if (channel.Value is not { } found)
            return new Found(Problem: TwitchErrors.ChannelNotFound);

        // Asked for the same reason the poll asks: it proves the app token may read the stream.
        var stream = await twitch.StreamAsync(accessToken, clientId, found.Id, ct);

        if (stream.Failure is { } streamFailure)
        {
            if (streamFailure.Problem == TwitchProblem.Unauthorized)
                signIn.Refused(accessToken);

            return Refused(streamFailure) with { ChannelId = found.Id, Name = found.DisplayName };
        }

        return new Found(found.Id, found.DisplayName);
    }

    private static Found Refused(TwitchFailure failure) => new(Problem: TwitchErrors.Sentence(failure), Failure: failure);

    private static void ClearCheck(Core.Data.Entities.Settings settings)
    {
        settings.TwitchCheckedAt = null;
        settings.TwitchChannelId = null;
        settings.TwitchChannelName = null;
        settings.TwitchProblem = null;
    }

    internal static TwitchSettingsView View(Core.Data.Entities.Settings settings, DateTimeOffset now)
    {
        var check = settings.TwitchCheckedAt is { } at
            ? new TwitchCheckView(at, settings.TwitchChannelName, settings.TwitchProblem)
            : null;

        return new TwitchSettingsView(
            settings.TwitchClientId,
            settings.TwitchClientSecretEncrypted is not null,
            settings.TwitchChannelLogin,
            check,
            settings.TwitchStoppedUntil is { } until && now < until ? until : null,
            settings.TwitchLiveOn,
            TwitchRules.SetUp(settings),
            settings.TwitchPolledAt,
            settings.TwitchPollProblem,
            settings.TwitchPostAfterMinutes,
            settings.TwitchPostEveryHours,
            settings.TwitchPostTitle ?? TwitchRules.DefaultTitle,
            settings.TwitchPostText ?? TwitchRules.DefaultText,
            TwitchPostPlaces.Parse(settings.TwitchPostPlaces),
            PostRequests.VRChatRoles(settings));
    }
}
