using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Api.Features.ApiKeys;
using Modbot.Api.Features.Proxy;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Webhooks;
using Modbot.Api.Features.Auth.Account;
using Modbot.Api.Features.Auth.Login;
using Modbot.Api.Features.Auth.Logout;
using Modbot.Api.Features.Auth.Me;
using Modbot.Api.Features.Auth.VRChatLink;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Features.DiscordSync;
using Modbot.Api.Features.DiscordLists;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.Mcp;
using Modbot.Api.Features.DiscordRoutes;
using Modbot.Api.Features.Reviews;
using Modbot.Api.Features.Roles;
using Modbot.Api.Features.Users;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Files;
using Modbot.Api.Features.Flags;
using Modbot.Api.Features.Health;
using Modbot.Api.Features.Imports;
using Modbot.Api.Features.Logs;
using Modbot.Api.Features.Members;
using Modbot.Api.Features.People;
using Modbot.Api.Features.Moderation;
using Modbot.Api.Features.Alerts;
using Modbot.Api.Features.Insights;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Live;
using Modbot.Api.Features.Live.Stream;
using Modbot.Api.Features.Places;
using Modbot.Api.Features.Search;
using Modbot.Api.Features.Server;
using Modbot.Api.Features.Onboarding.Complete;
using Modbot.Api.Features.Onboarding.CreateAdmin;
using Modbot.Api.Features.Onboarding.Integrations;
using Modbot.Api.Features.Onboarding.SelectGroup;
using Modbot.Api.Features.Onboarding.Status;
using Modbot.Api.Features.Onboarding.TestConnection;
using Modbot.Api.Features.Onboarding.VerifyVRChat;
using Modbot.Core;
using Modbot.Core.Discord;

namespace Modbot.Api;

/// <summary>
/// Registers Modbot's HTTP surface and its OpenAPI document.
/// </summary>
/// <remarks>
/// <para>
/// The document is generated from the endpoints themselves, so it cannot drift from what the server
/// actually serves — which is the whole reason for generating rather than hand-writing it. It feeds
/// three consumers: the documentation site, generated API clients, and anyone integrating against a
/// deployment through an <c>ApiKey</c>.
/// </para>
/// <para>
/// Feature slices live under <c>Features/</c> and register themselves here (spec 2.8). A slice owns
/// its endpoint, contracts, validation and handler in one folder.
/// </para>
/// </remarks>
public static class ApiSurface
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddModbotApi(this IServiceCollection services)
    {
        // Chat's loop and tools (AI chat design). Here rather than in the host because the tools are
        // this project's own read code, and they resolve everything scoped from the request.
        services.AddModbotChat();

        // The MCP server: the same tools for a person's own AI app (MCP server design).
        services.AddModbotMcp();

        // Discord account linking (design 2026-09-15). The signal is shared with the bot's role job,
        // which the host registers in the same container.
        services.AddScoped<Features.Auth.VRChatLink.VRChatBioCheck>();
        services.AddScoped<DiscordAccountLinks>();
        services.AddScoped<DiscordOAuth>();
        services.AddSingleton<LinkCookies>();
        services.AddSingleton<LinkCheckLimit>();
        services.TryAddSingleton<DiscordLinkSignal>();
        services.AddHttpClient(DiscordOAuth.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));

        // A host with the bot registers the real one first and wins; a host without it answers
        // that there is no bot rather than pretending there is nothing to sync.
        services.TryAddSingleton<IDiscordSyncRunner, NoDiscordSyncRunner>();

        // Whether this is a demo. The host decides it during startup and registers the decided one
        // before this runs; these are the fallbacks for a host that maps the API without demo mode,
        // and an undecided DemoMode is never on -- so the fallback cannot serve anybody as an
        // administrator (demo mode design §3).
        services.TryAddSingleton<Core.Configuration.DemoState>();
        services.TryAddSingleton(sp => Core.Configuration.DemoMode.From(
            sp.GetService<Core.Configuration.ModbotEnvironment>() ?? new Core.Configuration.ModbotEnvironment()));

        // Pictures and video fetched from VRChat on Modbot's session, and the disk cache that
        // keeps them (VRChat files design). The folder sits under the same /app/data mount the
        // evidence store uses, because it is the same disk.
        services.TryAddSingleton(sp => VRChatFileCacheOptions.Under(
            sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath
            ?? AppContext.BaseDirectory));
        services.TryAddSingleton(sp => new Features.Files.VRChatFileCache(
            sp.GetRequiredService<VRChatFileCacheOptions>(),
            sp.GetRequiredService<Modbot.Core.Time.IModbotClock>()));

        // Its wording, sign-in schemes, error shape and section order are in OpenApiReference.
        services.AddOpenApi(DocumentName, options => options.AddModbotReference());

        // The live event WebSocket (API keys design §5). Tickets and the connection count are held
        // in memory: a restart forgets both, and so do the connections they belong to.
        services.TryAddSingleton(new EventSocketOptions());
        services.TryAddSingleton<Modbot.Analytics.Facts.FactSignal>();
        services.TryAddSingleton<EventTickets>();
        services.TryAddSingleton<EventConnections>();

        // Webhooks (API keys design §6). The sender is here because "Send test" needs it; the
        // service that delivers on a timer is added by the host, with AddWebhookDelivery.
        services.TryAddSingleton(new WebhookOptions());
        services.TryAddSingleton(sp => new WebhookSender(
            sp.GetRequiredService<Modbot.Core.Time.IModbotClock>(),
            sp.GetRequiredService<WebhookOptions>(),
            sp.GetService<Modbot.VRChat.Scheduling.IMonotonicClock>()));
        services.TryAddScoped<WebhookDispatcher>();

        // Signing somebody up for the project's news when they tick the box while making their
        // account (server info and account email design §5). The Cloud client and the Cloud
        // address are the host's registrations: a host that maps the API without them gets the
        // subscriber that does nothing, and the checkbox is not shown.
        services.TryAddScoped<Modbot.Core.Cloud.IUpdatesSubscriber>(sp =>
        {
            var client = sp.GetService<Modbot.Core.Cloud.CloudServerClient>();
            var cloud = sp.GetService<Core.Configuration.ModbotCloudAddress>();
            var protector = sp.GetService<Core.Security.ISecretProtector>();

            if (client is null || cloud is null || protector is null)
                return new Modbot.Core.Cloud.NoUpdatesSubscriber();

            return new Modbot.Core.Cloud.CloudUpdatesSubscriber(
                sp.GetRequiredService<Core.Data.ModbotContext>(),
                client,
                cloud,
                protector,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Modbot.Core.Cloud.CloudUpdatesSubscriber>>());
        });

        // Imports of old data (import design §8): the upload endpoint queues, the hosted loop
        // runs, and the signal is how the first tells the second not to wait out its poll.
        services.TryAddSingleton<Features.Imports.ImportSignal>();
        services.TryAddScoped<Features.Imports.ImportRunner>();
        services.AddHostedService<Features.Imports.ImportService>();

        return services;
    }

    /// <summary>Delivers webhooks in the background. Needs the database, the clock and the secret protector.</summary>
    public static IServiceCollection AddWebhookDelivery(this IServiceCollection services)
    {
        services.AddHostedService<WebhookDeliveryService>();

        // Wakes WebSocket connections and long polls when a fact commits (API keys design §5.5).
        services.AddHostedService<FactFeedWatcher>();
        return services;
    }

    /// <summary>Sends queued email in the background. Needs what <see cref="Auth.ModbotAuth.AddModbotAuth"/> registers.</summary>
    public static IServiceCollection AddEmailQueue(this IServiceCollection services)
    {
        services.AddHostedService<Modbot.Core.Email.EmailQueueService>();
        return services;
    }

    /// <summary>
    /// Watches Modbot's own health and emails the staff accounts that asked. Registered by the host
    /// rather than by <see cref="AddModbotApi"/>, because the checker reads the gate, the Discord
    /// bot, AI spend and the log store, and only the host knows which of those it registered.
    /// </summary>
    public static IServiceCollection AddHealthAlerts(this IServiceCollection services)
    {
        services.AddScoped<Features.Health.Alerts.HealthAlertChecker>();
        services.AddHostedService<Features.Health.Alerts.HealthAlertService>();
        return services;
    }

    /// <summary>
    /// Routes that moved, answered at their old address. Before routing, so the endpoint that
    /// answers is the one at the new address and the API document lists each route once.
    /// </summary>
    /// <remarks>
    /// <c>/api/settings/ai/moderation</c> became <c>/api/settings/automod</c> when AutoMod got its
    /// own tab (AutoMod design §3). A rewrite rather than a second mapping, because a second
    /// mapping would be every handler twice under a second set of names.
    /// </remarks>
    public static IApplicationBuilder UseOldApiPaths(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Features.Settings.AutoModEndpoints.OldPath, out var rest))
                context.Request.Path = Features.Settings.AutoModEndpoints.Path + rest;

            return next(context);
        });

        // WebApplication inserts its automatic UseRouting() at the very start of the pipeline --
        // before every Use() the host calls, no matter where in source order it calls them (see
        // "Middleware added automatically by WebApplication"). Left implicit, routing would match
        // the request's original path before the rewrite above ever ran, and an old-path request
        // would 404. Calling UseRouting() here, right after the rewrite, makes this the position
        // route matching actually happens; WebApplication then does not add its own.
        return app.UseRouting();
    }

    public static IEndpointRouteBuilder MapModbotApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Version");

        api.MapGet("/version", () => Results.Ok(new VersionResponse(
                ModbotVersion.Release,
                ModbotVersion.Api,
                ModbotVersion.ApiMinimum)))
            .WithName("GetVersion")
            .WithSummary("Server and API version")
            .WithDescription(
                "Unauthenticated so a client can negotiate compatibility before it holds "
                + "credentials. Clients compare their own supported range against "
                + "apiVersionMinimum..apiVersion and use the highest both support.")
            .Produces<VersionResponse>();

        // Feature slices map themselves (spec 2.8); this list is the only place that knows they
        // all exist, and adding a feature is adding one line here plus one folder.
        app.MapLogin();
        app.MapLogout();
        app.MapMe();

        // Accounts and access (design 2026-09-13): the signed-in person's own account, the
        // required VRChat link, staff management, roles, invite and reset links.
        app.MapAccount();
        app.MapVRChatLink();
        app.MapUsers();
        app.MapInvites();
        app.MapResetLinks();
        app.MapRoles();

        // Keys for programs (API keys design §3). A key is accepted by every endpoint mapped here,
        // through the same authorisation a session goes through.
        app.MapApiKeys();

        // Requests forwarded to VRChat's own API, as the service account or with the caller's own
        // cookie, and the switch that turns the route on (VRChat proxy design).
        app.MapVRChatProxy();
        app.MapVRChatProxySettings();

        // Pictures and video fetched from VRChat, because a browser cannot fetch one itself
        // (VRChat files design).
        app.MapVRChatFiles();

        // Uploads of old data from another platform (import design §4).
        app.MapImports();

        // What this Modbot is, for my.modbot.co and anyone else who asks (server info and account
        // email design §2). Anonymous, and the one endpoint readable from another origin.
        app.MapServerInfo();

        app.MapDataSettings();
        app.MapSyncSettings();
        app.MapPublicAddressSettings();
        app.MapServerSettings();
        app.MapPublicInstancesSettings();
        app.MapRepeatOffenderSettings();
        app.MapCloudSettings();
        app.MapUpdateSettings();
        app.MapEmailSettings();
        app.MapAiSettings();
        app.MapAiChatSettings();
        app.MapAutoModSettings();
        app.MapModerationFlags();
        app.MapAiLimitsSettings();
        app.MapAiCatalog();
        app.MapAiCallLog();

        // The Discord server's channels and roles as the bot last stored them, so a setting picks
        // a channel by name and sees which permission the bot lacks there (M5 spec §7).
        app.MapDiscordLists();
        app.MapDiscordRoutes();

        // The Discord server's members, current and past, as the bot keeps them.
        app.MapDiscordMembers();

        // AI insights: reading them, and when they are written (AI insights design).
        app.MapInsights();

        // Unusual-activity alerts and what is watched for them (AI insights design §8).
        app.MapAlerts();

        // Linking a member's Discord and VRChat accounts: the public link page's API, the moderator's
        // view and unlink, and the settings (Discord account linking design).
        app.MapDiscordLink();
        app.MapDiscordLinkModeration();
        app.MapDiscordLinkingSettings();

        // Which group role goes with which Discord role, which side decides, and whether bans
        // cross over (M5 §3, §4).
        app.MapDiscordSync();

        // The read surface over the fact log and the daily totals derived from it. Sync health resolves
        // SyncDiagnostics optionally, so a host that maps the API without registering the
        // producers still starts and still answers -- it reports that nothing is syncing here
        // rather than failing to map.
        app.MapAuditLog();
        app.MapAnalytics();
        app.MapSyncHealth();

        // How hard the machine itself is working, for the bottom of Host & Database (machine usage
        // design). Resolves its sampler optionally, so a host without the background services
        // answers with an empty window rather than failing at request time.
        app.MapMachineUsage();

        // Modbot's own log, for a deployment with no Seq and no disk that survives a redeploy.
        app.MapLogs();

        // The emails Modbot sends about its own health, and what it watches.
        Features.Health.Alerts.HealthAlertEndpoints.MapHealthAlerts(app);

        // The people the project thanks, read from Modbot Cloud for the Credits page.
        Features.Credits.CreditsEndpoints.MapCredits(app);

        app.MapEvidence();

        // One VRChat user's stored profile and the 18+ flag (user profile sync design §6). The
        // queue and the record writer resolve optionally, like SyncDiagnostics does above.
        app.MapVRChatUsers();

        // One person's VRChat, Discord and Modbot accounts, tied together from whichever one a
        // link named, so the popup opens on the human being rather than on one of their accounts
        // (one view per person design §3). Its own address under /api/people, because the list
        // below answers a different question and the two were built at the same time.
        app.MapPersonLookup();

        // One world and one instance, for the popup that opens when somebody clicks either (spec
        // 10.2). Read entirely from Modbot's own tables -- opening a popup costs no VRChat
        // budget, however often a moderator does it.
        app.MapPlaces();

        // People, Discord people and worlds by name, for the command palette. One round trip,
        // narrowed per kind by the permission that gates the page each would be found on.
        app.MapSearch();

        // Live: the group's open instances right now and who is in each. From Modbot's own tables
        // only, so a page that refreshes every five seconds costs no VRChat budget.
        app.MapLive();

        // Live updates: the Live page's and the notifications' stream, a WebSocket with long
        // polling behind it (live updates design). The host must call UseWebSockets before this.
        app.MapLiveStream();

        // Chat: questions answered by the configured model, using tools that run with the asking
        // person's own permissions (AI chat design §3.1). Conversations are the owner's alone.
        app.MapChat();
        app.MapModbotMcp();

        // Every new fact, as it is written, to a connected program (API keys design §5). The host
        // must call UseWebSockets before mapping this.
        app.MapEvents();

        // The same stream by long polling, for programs that can hold neither (API keys design §5.6).
        app.MapEventPoll();

        // The same events, sent to addresses the operator registers (API keys design §6).
        app.MapWebhooks();

        // Moderation accountability (spec 5.8): people acted on more than once, and the reviews
        // that open when a moderator's pattern looks unusual. Read from caches the review job
        // rebuilds; closing a review resolves ReviewFacts optionally, like the sync pieces above.
        app.MapRepeatOffenders();
        app.MapReviews();

        // The member list and the ban list as the sweeps last read them (member and ban sync
        // design §5), with search.
        app.MapMembers();

        // Everyone Modbot has a record of, member or not: the rest of vrchat_user, which the
        // member list by definition leaves out (everyone Modbot has seen design).
        app.MapPeople();

        // Ban case files (spec 5.8.3): the write-up of each ban, and the reason list moderators
        // pick from. The fact writer and the profile sync's recorder resolve optionally, like
        // the review close does; a host without them reads case files and refuses to write one.
        app.MapBanReasons();
        app.MapCaseFiles();

        // Kick, ban and unban (M4 §4). The only endpoints that change anything in VRChat, so the
        // gate and the fact log resolve optionally and a host without them refuses to act rather
        // than pretending to.
        app.MapModerationActions();

        // Planned events, and the calendar feed (calendar design). Publishing and opening happen in
        // the calendar's own loops; these only store what a person decides.
        Features.Calendar.CalendarEndpoints.MapCalendar(app);

        // Giveaways: their rules, who entered, and the draws (giveaways design). Drawing happens
        // here because a person pressed Draw; a draw whose time simply came round is made by the
        // giveaway's own loop, through the same drawer.
        Features.Giveaways.GiveawayEndpoints.MapGiveaways(app);

        // Whether this deployment is a demo, and the control that puts its data back (demo mode
        // design §6). Mapped everywhere; on anything but a demo it answers "no" and refuses the
        // reset, because the web app asks it on every load to decide whether to show the marker.
        Features.Demo.DemoEndpoints.MapDemo(app);

        // Onboarding (spec 7.1). Each step is its own slice because each one is independently
        // re-runnable from settings later -- they are not stages of a single transaction, and
        // modelling them as one endpoint with a step counter would make the "re-run just the
        // connection check" case the awkward one instead of the ordinary one.
        app.MapOnboardingStatus();
        app.MapCreateAdmin();
        app.MapVerifyVRChat();
        app.MapTestConnection();
        app.MapSelectGroup();
        app.MapIntegrations();
        app.MapCompleteOnboarding();

        return app;
    }
}

/// <param name="Version">Calendar release version, <c>YYYY.M.PATCH</c>.</param>
/// <param name="ApiVersion">Highest API version this server speaks.</param>
/// <param name="ApiVersionMinimum">Lowest API version this server still speaks.</param>
public sealed record VersionResponse(string Version, int ApiVersion, int ApiVersionMinimum);
