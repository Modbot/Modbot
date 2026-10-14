using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Alerts;

/// <summary>The Open second instance button under an "open a second instance" alert.</summary>
public static class SecondInstanceButton
{
    public const string Label = "Open second instance";

    private const string Prefix = DiscordActionButton.Prefix + "second:";

    /// <summary>The id of the button for one alert: this and the alert's id.</summary>
    public static string For(Guid alertId) => Prefix + alertId.ToString("D");

    /// <summary>Whether a pressed button is one of these.</summary>
    public static bool Is(string buttonId)
    {
        ArgumentNullException.ThrowIfNull(buttonId);
        return buttonId.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>The alert a button names, or null when the id is not one of these.</summary>
    public static Guid? AlertOf(string buttonId)
    {
        ArgumentNullException.ThrowIfNull(buttonId);

        return Is(buttonId) && Guid.TryParseExact(buttonId[Prefix.Length..], "D", out var id) ? id : null;
    }
}

/// <summary>
/// A press on Open second instance: staff only, then <see cref="ISecondInstances"/> does the opening.
/// </summary>
/// <remarks>
/// Answered to the person who pressed, with VRChat's launch page for the new instance as a link
/// button. The permission is checked here for the sentence and again by the opener.
/// </remarks>
public sealed class SecondInstancePress(ModbotContext db, IModbotClock clock, ISecondInstances? instances = null)
{
    public const string NotSetUp = "This deployment is not set up to act in VRChat.";
    public const string JoinLabel = "Join";

    public async Task<DiscordReply> PressAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (SecondInstanceButton.AlertOf(press.ButtonId) is not { } alertId)
            return DiscordReply.Say("Modbot does not know that button.");

        var user = await StaffDiscord.AccountForAsync(db, press.DiscordUserId, clock.UtcNow, ct).ConfigureAwait(false);

        if (user is null)
            return DiscordReply.Say(DiscordCommandHandler.NotLinkedMessage);

        if (user.IsDisabled)
            return DiscordReply.Say("Your Modbot account is disabled.");

        // The web app refuses every request from an account with no VRChat link; so does this.
        if (!user.IsVRChatLinked)
            return DiscordReply.Say(Interactions.StaffInteractionHandler.NeedsVRChatMessage);

        if (!DiscordCommands.Allows(user.EffectivePermissions, ModbotPermissions.ManageCalendar))
            return DiscordReply.Say("You need the \"Manage calendar\" permission in Modbot.");

        if (instances is null)
            return DiscordReply.Say(NotSetUp);

        var answer = await instances.OpenAsync(alertId, CommandAccess.Member(user), ct).ConfigureAwait(false);

        var text = CardText.EscapeText(answer.Message);

        return answer.JoinLink is { Length: > 0 } link
            ? new DiscordReply(text, [], Links: [new DiscordLinkButton(JoinLabel, link)])
            : DiscordReply.Say(text);
    }
}
