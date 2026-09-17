using System.Text.Json.Serialization;

namespace Modbot.Api.Features.Onboarding.Status;

/// <summary>
/// Which wizard steps a route is allowed to land on.
/// </summary>
/// <remarks>
/// Computed on the server rather than derived in the browser, because "what is configured" is a
/// database question and because spec 7.1's steps are <em>independently re-runnable</em> — the
/// wizard is not a one-way sequence with a counter, it is a set of steps with a suggested order.
/// The SPA resumes at <see cref="OnboardingStatusResponse.NextStep"/> and is free to navigate to
/// any completed one.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OnboardingStep>))]
public enum OnboardingStep
{
    /// <summary>Spec 7.1 step 1 — the first staff account.</summary>
    Administrator,

    /// <summary>Spec 7.1 step 2 — the VRChat account Modbot acts as.</summary>
    VRChat,

    /// <summary>Spec 7.1 step 3 / 7.1.1 — can this host reach the API at all.</summary>
    Connection,

    /// <summary>
    /// Accounts and access design §4.3 — the administrator links their own VRChat account.
    /// After the connection check because the proof goes through the gate.
    /// </summary>
    LinkVRChat,

    /// <summary>Spec 7.1 step 4 — which group this deployment manages.</summary>
    Group,

    /// <summary>Spec 7.1 step 5 — Discord and SMTP, both skippable.</summary>
    Optional,

    /// <summary>Nothing left to do.</summary>
    Done,
}

/// <param name="VerifiedAt">When VRChat last accepted these credentials.</param>
/// <param name="DisplayName">The account VRChat said they belong to.</param>
/// <param name="LastSignedInAt">When Modbot last signed in to VRChat with the password (spec 4.1.2).</param>
public sealed record VRChatAccountStatus(
    string? Username, string? DisplayName, DateTimeOffset? VerifiedAt, DateTimeOffset? LastSignedInAt = null);

/// <param name="ProxyUrl">
/// The configured egress proxy, or null. The <em>password</em> is never returned — spec 5.9.3 and
/// 4.4.1 — but the URL and username are, so re-running the step pre-fills instead of asking the
/// operator to retype something they cannot read back from anywhere.
/// </param>
public sealed record ConnectionStatus(
    DateTimeOffset? CheckedAt, string? ProxyUrl, string? ProxyUsername, bool ProxyPasswordStored);

/// <param name="IconUrl">The group's icon, as VRChat last showed it, or null.</param>
/// <param name="BannerUrl">The group's banner, or null.</param>
public sealed record ManagedGroupStatus(string Id, string Name, string? IconUrl = null, string? BannerUrl = null);

/// <param name="DiscordConfigured">Whether a bot token is stored. The token itself never leaves.</param>
/// <param name="DiscordInstanceChannelId">The channel open instances are announced in, or null.</param>
/// <param name="DiscordInstanceMessage">The line posted above each instance card, or null.</param>
/// <param name="DiscordInstanceShowNames">Whether a card lists who is in a watched instance.</param>
/// <param name="PublicAddress">The saved public address, or null (accounts and access design §4.2).</param>
/// <param name="PublicAddressSuggestion">
/// What the platform says the address is, for the form to prefill. A person confirms it; the
/// server never adopts it on its own.
/// </param>
public sealed record IntegrationStatus(
    bool DiscordConfigured,
    string? DiscordGuildId,
    string? DiscordInstanceChannelId,
    string? DiscordInstanceMessage,
    bool DiscordInstanceShowNames,
    bool SmtpConfigured,
    string? SmtpHost,
    string? PublicAddress,
    string? PublicAddressSuggestion);

/// <summary>
/// Everything the wizard needs to decide what to show, and nothing that is a secret.
/// </summary>
/// <param name="HasAdministrator">
/// False only on a genuinely fresh deployment. While it is false the wizard is open to anyone who
/// can reach the URL, which is exactly what spec 7.1 intends and exactly why it stops being true
/// the moment the first account exists.
/// </param>
/// <param name="Authenticated">Whether the caller currently holds a session.</param>
/// <param name="OnboardingComplete">Whether the operator has finished the wizard at least once.</param>
/// <param name="NextStep">Where to resume.</param>
/// <param name="VRChatLinked">
/// Whether the signed-in account has linked its VRChat account (design §4.3). False when nobody is
/// signed in. The wizard's link step is done when this is true.
/// </param>
/// <param name="MyModbotUrl">
/// Where my.modbot.co is, from <c>MODBOT_MY_URL</c>. Every link the app offers to the selector is
/// built from it, so a group that runs its own points them all somewhere else with one variable.
/// </param>
public sealed record OnboardingStatusResponse(
    bool HasAdministrator,
    bool Authenticated,
    bool VRChatLinked,
    bool OnboardingComplete,
    OnboardingStep NextStep,
    VRChatAccountStatus VRChat,
    ConnectionStatus Connection,
    ManagedGroupStatus? Group,
    IntegrationStatus Integrations,
    string MyModbotUrl);
