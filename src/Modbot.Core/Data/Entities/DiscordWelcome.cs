namespace Modbot.Core.Data.Entities;

/// <summary>Where the smart welcome goes (smart welcome design §2).</summary>
public static class DiscordWelcomeWhere
{
    public const string DirectMessage = "dm";

    public const string Channel = "channel";

    public const string Both = "both";

    public static bool IsKnown(string? value) => value is DirectMessage or Channel or Both;

    public static bool UsesChannel(string? value) => value is Channel or Both;

    public static bool UsesDirectMessage(string? value) => value is DirectMessage or Both;
}

/// <summary>What the smart welcome is limited to, in one place for the page, the save and the send.</summary>
public static class DiscordWelcomeLimits
{
    /// <summary>The most steps one member is sent.</summary>
    public const int MostSteps = 3;

    public const int MostRules = 25;

    public const int MostDefaults = MostSteps;

    /// <summary>A step's text. Also what a link button's label may hold, which Discord ends at 80.</summary>
    public const int MostTextLength = 80;

    public const int MostLinkLength = 500;
}

/// <summary>Which list a <see cref="DiscordWelcomeStep"/> belongs to.</summary>
public static class DiscordWelcomeStepKinds
{
    /// <summary>"If they hold this role, send this step."</summary>
    public const string Rule = "rule";

    /// <summary>Sent when fewer than three rules matched.</summary>
    public const string Default = "default";
}

/// <summary>
/// One step the smart welcome can send: a short text and an optional link. The table is
/// <c>discord_welcome_step</c> (smart welcome design §7).
/// </summary>
public class DiscordWelcomeStep
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>One of <see cref="DiscordWelcomeStepKinds"/>.</summary>
    public string Kind { get; set; } = DiscordWelcomeStepKinds.Rule;

    /// <summary>For a rule, the Discord role a member must hold. Text, never parsed. Null for a default.</summary>
    public string? RoleId { get; set; }

    /// <summary>What the step says. Written by an admin.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>An https address the step opens, or null.</summary>
    public string? Link { get; set; }

    /// <summary>The step's place among the others of its kind, from 0.</summary>
    public int Position { get; set; }
}

/// <summary>What became of a member's welcome.</summary>
public static class DiscordWelcomeStates
{
    /// <summary>The member joined while the join gate held them; the welcome goes when it lets them in.</summary>
    public const string Waiting = "waiting";

    /// <summary>The welcome was claimed and sent (or tried).</summary>
    public const string Sent = "sent";
}

/// <summary>
/// One row per member the smart welcome has seen. It is what makes a welcome go out once per
/// member, however the events race (smart welcome design §5.1). The table is <c>discord_welcome</c>.
/// </summary>
public class DiscordWelcome
{
    /// <summary>The Discord account. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>One of <see cref="DiscordWelcomeStates"/>.</summary>
    public string State { get; set; } = DiscordWelcomeStates.Waiting;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}
