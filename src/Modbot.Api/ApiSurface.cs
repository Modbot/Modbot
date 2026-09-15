using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Api.Features.ApiKeys;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Auth.Account;
using Modbot.Api.Features.Auth.Login;
using Modbot.Api.Features.Auth.Logout;
using Modbot.Api.Features.Auth.Me;
using Modbot.Api.Features.Auth.VRChatLink;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Features.DiscordLists;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.DiscordRoutes;
using Modbot.Api.Features.Reviews;
using Modbot.Api.Features.Roles;
using Modbot.Api.Features.Users;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Flags;
using Modbot.Api.Features.Health;
using Modbot.Api.Features.Members;
using Modbot.Api.Features.Insights;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Live;
using Modbot.Api.Features.Places;
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

        // Discord account linking (design 2026-09-15). The signal is shared with the bot's role job,
        // which the host registers in the same container.
        services.AddScoped<Features.Auth.VRChatLink.VRChatBioCheck>();
        services.AddScoped<DiscordAccountLinks>();
        services.AddScoped<DiscordOAuth>();
        services.AddSingleton<LinkCookies>();
        services.AddSingleton<LinkCheckLimit>();
        services.TryAddSingleton<DiscordLinkSignal>();
        services.AddHttpClient(DiscordOAuth.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));

        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new()
                {
                    Title = "Modbot API",
                    Version = ModbotVersion.Release,
                    Description =
                        "Cached VRChat group data, moderation history and analytics for a single "
                        + "group.\n\n"
                        + "This is one self-hosted deployment's API. There is no central Modbot "
                        + "service and no shared endpoint — you are talking to somebody's own "
                        + "server.\n\n"
                        + $"API version {ModbotVersion.Api} (minimum supported "
                        + $"{ModbotVersion.ApiMinimum}). The API version is a plain integer, "
                        + "separate from the calendar release version, and increments only on a "
                        + "breaking change.",
                };
                return Task.CompletedTask;
            });
        });

        // The live event WebSocket (API keys design §5). Tickets and the connection count are held
        // in memory: a restart forgets both, and so do the connections they belong to.
        services.TryAddSingleton(new EventSocketOptions());
        services.TryAddSingleton<EventTickets>();
        services.TryAddSingleton<EventConnections>();

        return services;
    }

    public static IEndpointRouteBuilder MapModbotApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Meta");

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

        app.MapDataSettings();
        app.MapSyncSettings();
        app.MapPublicAddressSettings();
        app.MapEmailSettings();
        app.MapAiSettings();
        app.MapAiChatSettings();
        app.MapAiModerationSettings();
        app.MapModerationFlags();

        // The Discord server's channels and roles as the bot last stored them, so a setting picks
        // a channel by name and sees which permission the bot lacks there (M5 spec §7).
        app.MapDiscordLists();
        app.MapDiscordRoutes();

        // AI insights: reading them, and when they are written (AI insights design).
        app.MapInsights();

        // Linking a member's Discord and VRChat accounts: the public link page's API, the moderator's
        // view and unlink, and the settings (Discord account linking design).
        app.MapDiscordLink();
        app.MapDiscordLinkModeration();
        app.MapDiscordLinkingSettings();

        // The read surface over the fact log and the daily totals derived from it. Sync health resolves
        // SyncDiagnostics optionally, so a host that maps the API without registering the
        // producers still starts and still answers -- it reports that nothing is syncing here
        // rather than failing to map.
        app.MapAuditLog();
        app.MapAnalytics();
        app.MapSyncHealth();
        app.MapEvidence();

        // One VRChat user's stored profile and the 18+ flag (user profile sync design §6). The
        // queue and the record writer resolve optionally, like SyncDiagnostics does above.
        app.MapVRChatUsers();

        // One world and one room, for the popup that opens when somebody clicks either (spec
        // 10.2). Read entirely from Modbot's own tables -- opening a popup costs no VRChat
        // budget, however often a moderator does it.
        app.MapPlaces();

        // Live: the group's open instances right now and who is in each. From Modbot's own tables
        // only, so a page that refreshes every five seconds costs no VRChat budget.
        app.MapLive();

        // Chat: questions answered by the configured model, using tools that run with the asking
        // person's own permissions (AI chat design §3.1). Conversations are the owner's alone.
        app.MapChat();

        // Every new fact, as it is written, to a connected program (API keys design §5). The host
        // must call UseWebSockets before mapping this.
        app.MapEvents();

        // Moderation accountability (spec 5.8): people acted on more than once, and the reviews
        // that open when a moderator's pattern looks unusual. Read from caches the review job
        // rebuilds; closing a review resolves ReviewFacts optionally, like the sync pieces above.
        app.MapRepeatOffenders();
        app.MapReviews();

        // The member list and the ban list as the sweeps last read them (member and ban sync
        // design §5), with search.
        app.MapMembers();

        // Ban case files (spec 5.8.3): the write-up of each ban, and the reason list moderators
        // pick from. The fact writer and the profile sync's recorder resolve optionally, like
        // the review close does; a host without them reads case files and refuses to write one.
        app.MapBanReasons();
        app.MapCaseFiles();

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
