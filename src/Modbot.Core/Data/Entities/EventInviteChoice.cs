namespace Modbot.Core.Data.Entities;

/// <summary>
/// A member's own answer to "Get event invites?", given with the <c>/me</c> buttons in Discord. The
/// table is <c>event_invite_choice</c> (calendar auto-invite design §2.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nobody on a list is invited without one.</strong> Invites to an event's instance go only
/// to people who asked for them (user, 2026-10-01): a VRChat invite or a direct message nobody asked
/// for is the risk a Terms of Service review named. No row, or <see cref="Wants"/> false, means no.
/// </para>
/// <para>
/// Keyed on the Discord account that pressed the button, because that is who asked. The linked
/// VRChat account at that moment is kept beside it, so the person is found whichever id a list gives
/// for them. The ids are opaque text, never validated (foundation §3.1.1). A purge deletes the row.
/// </para>
/// </remarks>
public class EventInviteChoice
{
    /// <summary>The Discord account that pressed the button. A snowflake, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>The VRChat account linked to it when the choice was made, if any.</summary>
    public string? VRChatUserId { get; set; }

    /// <summary>True for "Get event invites", false after "Stop event invites".</summary>
    public bool Wants { get; set; }

    /// <summary>When the person last changed it, from <c>IModbotClock</c>.</summary>
    public DateTimeOffset ChangedAt { get; set; }
}
