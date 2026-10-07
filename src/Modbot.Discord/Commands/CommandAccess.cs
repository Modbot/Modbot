using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// Who is pressing, and the record of it, for the steps of <c>/event</c> and <c>/post</c> that come
/// after the command itself: the form, the confirmation, the press (Discord commands design §3.1
/// and §3.9).
/// </summary>
/// <remarks>
/// The command runs the same checks in <see cref="DiscordCommandHandler"/>; this is that rule again
/// for a press or a form, because a role taken away between the command and the confirmation is a
/// refusal, never an action. The order is the design's: no account, disabled, no VRChat link (a
/// step that writes), then the permission.
/// </remarks>
public static class CommandAccess
{
    /// <summary>
    /// The staff account behind this Discord account, or the reply that refuses it and the outcome
    /// to record.
    /// </summary>
    /// <param name="command">The command's name, for the permission sentence.</param>
    public static async Task<(ModbotUser? User, DiscordReply? Refusal, string Outcome)> StaffAsync(
        ModbotContext db,
        IModbotClock clock,
        string discordUserId,
        ModbotPermissions required,
        bool writes,
        string command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var user = await StaffDiscord.AccountForAsync(db, discordUserId, clock.UtcNow, ct).ConfigureAwait(false);

        if (user is null)
            return (null, DiscordReply.Say(DiscordCommandHandler.NotLinkedMessage), "not-linked");

        if (user.IsDisabled)
            return (user, DiscordReply.Say("Your Modbot account is disabled."), "disabled");

        // The web app refuses every request from an account with no VRChat link
        // (VRChatLinkedRequirement); anything here that writes does the same.
        if (writes && !user.IsVRChatLinked)
            return (user, DiscordReply.Say(Interactions.StaffInteractionHandler.NeedsVRChatMessage), "no-vrchat");

        if (!DiscordCommands.Allows(user.EffectivePermissions, required))
        {
            return (
                user,
                DiscordReply.Say($"You need the \"{DiscordCommands.Label(required)}\" permission in Modbot to use /{command}."),
                "no-permission");
        }

        return (user, null, "answered");
    }

    public static StaffMember Member(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new StaffMember(user.Id, user.Username, user.EffectivePermissions);
    }

    /// <summary>The access record a slash command leaves (<c>modbot.discord.command</c>), for a form or a press.</summary>
    /// <param name="more">Further fields: what the step was about.</param>
    public static async Task RecordAsync(
        IFactWriter facts,
        IModbotClock clock,
        string discordUserId,
        ModbotUser? user,
        string command,
        string outcome,
        JsonObject? more,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);

        var data = new JsonObject
        {
            ["command"] = command,
            ["outcome"] = outcome,
        };

        if (more is not null)
        {
            foreach (var (name, value) in more)
                data[name] = value?.DeepClone();
        }

        await facts.WriteAsync(new FactRecord
            {
                Type = FactType.DiscordCommandRun,
                OccurredAt = clock.UtcNow,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = discordUserId,
                ActorPlatform = user is null ? null : FactPlatform.Modbot,
                ActorId = user?.Id.ToString(),
                Source = FactSource.Discord,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }
}
