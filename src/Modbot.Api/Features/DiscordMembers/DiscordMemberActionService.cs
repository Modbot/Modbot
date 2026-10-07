using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Conventions;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordMembers;

/// <summary>What the service was asked to do to a member of the Discord server.</summary>
public enum DiscordMemberVerb
{
    Ban = 1,
    Unban = 2,
    Kick = 3,
    TimeOut = 4,
}

/// <summary>How a Discord action ended, in the words the API answers with.</summary>
public enum DiscordMemberActionStatus
{
    /// <summary>Discord did it, or it was already so. See <see cref="DiscordMemberActionResult.Changed"/>.</summary>
    Done = 0,

    /// <summary>This deployment cannot record actions, so nothing was asked of Discord. 503.</summary>
    CannotRecord = 1,

    /// <summary>Nobody was named. 400.</summary>
    NoOne = 2,

    /// <summary>No Discord server is set up. 409.</summary>
    NoServer = 3,

    /// <summary>The bot, the server's owner or a staff account: never acted on from here. 403.</summary>
    OffLimits = 4,

    /// <summary>The bot is not connected, so nothing was asked of Discord. 503.</summary>
    BotOffline = 5,

    /// <summary>Discord was asked and refused. 502.</summary>
    DiscordRefused = 6,
}

/// <summary>
/// What a Discord action did.
/// </summary>
/// <param name="Message">The sentence for any status but <see cref="DiscordMemberActionStatus.Done"/>.</param>
/// <param name="Changed">False when it was already so: not banned, already banned, not in the server.</param>
public sealed record DiscordMemberActionResult(DiscordMemberActionStatus Status, string? Message = null, bool Changed = false)
{
    public bool Done => Status == DiscordMemberActionStatus.Done;

    /// <summary>The HTTP status the API answers with.</summary>
    public int HttpStatus => Status switch
    {
        DiscordMemberActionStatus.Done => StatusCodes.Status200OK,
        DiscordMemberActionStatus.CannotRecord => StatusCodes.Status503ServiceUnavailable,
        DiscordMemberActionStatus.NoOne => StatusCodes.Status400BadRequest,
        DiscordMemberActionStatus.NoServer => StatusCodes.Status409Conflict,
        DiscordMemberActionStatus.OffLimits => StatusCodes.Status403Forbidden,
        DiscordMemberActionStatus.BotOffline => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway,
    };

    /// <summary>The problem code the API answers with, or null for <see cref="DiscordMemberActionStatus.Done"/>.</summary>
    public string? Code => Status switch
    {
        DiscordMemberActionStatus.Done => null,
        DiscordMemberActionStatus.CannotRecord => Problems.NotSetUp,
        DiscordMemberActionStatus.NoOne => null,
        DiscordMemberActionStatus.NoServer => Problems.NotSetUp,
        DiscordMemberActionStatus.OffLimits => Problems.Refused,
        DiscordMemberActionStatus.BotOffline => Problems.Unavailable,
        _ => Problems.DiscordRefused,
    };
}

/// <summary>
/// Ban, unban, remove and time out one member of the Discord server, once, for a named Modbot
/// account: the one place the rules live, for the API's endpoints and for the bot's own commands
/// (Discord commands design §3.3, step 3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Moved out of the endpoints, not changed.</strong> The four <c>/api/discord</c> endpoints
/// used to hold this in a private method. The bot's <c>/ban</c> and <c>/kick</c> need the same
/// refusals and the same fact, and a copy of them would drift, so the endpoints now call this and
/// answer with what it says. Every status, sentence and code is the one they gave before.
/// </para>
/// <para>
/// <strong>One request, never retried, and nothing recorded unless Discord accepted.</strong> Then a
/// <c>modbot.action.discord.*</c> fact names the Modbot account that asked, beside Discord's own
/// audit entry, which can only name the bot. The reason goes to Discord's audit log as
/// "Modbot: banned by &lt;account&gt;: &lt;reason&gt;".
/// </para>
/// <para>
/// <strong>Three accounts are never acted on from here</strong> (TASK-058): Modbot's own bot, the
/// server's owner, and any Discord account linked to a Modbot staff account.
/// </para>
/// </remarks>
public sealed class DiscordMemberActionService
{
    /// <summary>Discord keeps at most this many characters of a reason in its audit log.</summary>
    public const int MaxReasonLength = 512;

    public const string BotMessage = "That is the Discord account Modbot's bot runs as. Modbot will not act on itself.";
    public const string OwnerMessage = "That is the owner of the Discord server. Modbot will not act on them.";
    public const string StaffMessage = "That Discord account belongs to a Modbot staff account. Modbot will not act on staff.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IDiscordMemberActions _discord;
    private readonly IFactWriter? _facts;
    private readonly EventPartitionMaintainer? _partitions;

    public DiscordMemberActionService(
        ModbotContext db,
        IModbotClock clock,
        IDiscordMemberActions discord,
        IFactWriter? facts,
        EventPartitionMaintainer? partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(discord);

        _db = db;
        _clock = clock;
        _discord = discord;
        _facts = facts;
        _partitions = partitions;
    }

    /// <summary>The permission each action needs, apart from the VRChat one of the same name.</summary>
    public static ModbotPermissions Requires(DiscordMemberVerb verb) => verb switch
    {
        DiscordMemberVerb.Ban => ModbotPermissions.DiscordBan,
        DiscordMemberVerb.Unban => ModbotPermissions.DiscordUnban,
        DiscordMemberVerb.Kick => ModbotPermissions.DiscordKick,
        _ => ModbotPermissions.DiscordTimeOut,
    };

    /// <summary>
    /// Does the action. The order of the checks is the API's: a deployment that cannot record, then
    /// nobody named, then no server, then the three off-limits accounts, and only then Discord.
    /// </summary>
    /// <param name="actor">The Modbot account that asked.</param>
    /// <param name="actorName">Its name, for Discord's audit log; null reads "a Modbot account".</param>
    /// <param name="userId">The Discord account acted on.</param>
    /// <param name="why">Why, for Discord's audit log and Modbot's.</param>
    /// <param name="deleteMessageDays">For a ban: how many days of their messages Discord deletes too, 0 to 7.</param>
    /// <param name="timeout">For a time out: how long.</param>
    public async Task<DiscordMemberActionResult> ActAsync(
        DiscordMemberVerb verb,
        Guid actor,
        string? actorName,
        string? userId,
        string? why,
        int deleteMessageDays,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        if (_facts is null || _partitions is null)
            return new(DiscordMemberActionStatus.CannotRecord, "This deployment cannot record actions.");

        if (string.IsNullOrWhiteSpace(userId))
            return new(DiscordMemberActionStatus.NoOne, "Say who, by their Discord id.");

        var guildId = await GuildIdAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(guildId))
            return new(DiscordMemberActionStatus.NoServer, "No Discord server is set up yet.");

        // Three accounts are never acted on from here, however the request is worded. Modbot's own
        // bot (taking it out of the server would end every Discord feature, from inside the thing
        // doing it, as ModerationActionService refuses for Modbot's VRChat account), the server's
        // owner (whom Discord refuses anyway, and a refusal here is plainer), and any Discord account
        // linked to a Modbot staff account (a key that may time people out is not a way to lock
        // another moderator out of the server).
        if (await OffLimitsAsync(guildId, userId, ct).ConfigureAwait(false) is { } refusal)
            return refusal;

        var verbWord = verb switch
        {
            DiscordMemberVerb.Ban => "banned",
            DiscordMemberVerb.Unban => "unbanned",
            DiscordMemberVerb.Kick => "removed",
            _ => "timed out",
        };

        var reason = Reason(verbWord, actorName ?? "a Modbot account", why);

        var data = new JsonObject();

        DiscordMemberOutcome outcome;

        switch (verb)
        {
            case DiscordMemberVerb.Ban:
                outcome = await _discord.BanAsync(guildId, userId, reason, deleteMessageDays, ct).ConfigureAwait(false);
                data["deleteMessageDays"] = deleteMessageDays;
                break;
            case DiscordMemberVerb.Unban:
                outcome = await _discord.UnbanAsync(guildId, userId, reason, ct).ConfigureAwait(false);
                break;
            case DiscordMemberVerb.Kick:
                outcome = await _discord.KickAsync(guildId, userId, reason, ct).ConfigureAwait(false);
                break;
            default:
            {
                var duration = timeout ?? TimeSpan.Zero;
                outcome = await _discord.TimeOutAsync(guildId, userId, duration, reason, ct).ConfigureAwait(false);
                data["minutes"] = (int)duration.TotalMinutes;
                data["until"] = _clock.UtcNow + duration;
                break;
            }
        }

        if (outcome.BotOffline)
            return new(DiscordMemberActionStatus.BotOffline, outcome.Error);

        if (!outcome.Done)
            return new(DiscordMemberActionStatus.DiscordRefused, outcome.Error ?? "Discord refused.");

        if (!outcome.NothingToDo)
        {
            var now = _clock.UtcNow;
            await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

            data["reason"] = string.IsNullOrWhiteSpace(why) ? null : why.Trim();
            data["guildId"] = guildId;

            await _facts.WriteAsync(
                    new FactRecord
                    {
                        Type = verb switch
                        {
                            DiscordMemberVerb.Ban => FactType.ActionDiscordBan,
                            DiscordMemberVerb.Unban => FactType.ActionDiscordUnban,
                            DiscordMemberVerb.Kick => FactType.ActionDiscordKick,
                            _ => FactType.ActionDiscordTimeOut,
                        },
                        OccurredAt = now,
                        SubjectPlatform = FactPlatform.Discord,
                        SubjectId = userId,
                        ActorPlatform = FactPlatform.Modbot,
                        ActorId = actor.ToString(),
                        Source = FactSource.Manual,
                        Data = data,
                    },
                    ct)
                .ConfigureAwait(false);
        }

        return new(DiscordMemberActionStatus.Done, null, !outcome.NothingToDo);
    }

    /// <summary>
    /// The refusal for an account that may not be acted on, or null when it may: the checks that come
    /// before Discord is asked, so the bot can say no before it shows a confirmation. Nothing is
    /// asked of Discord and nothing is written.
    /// </summary>
    /// <remarks>
    /// Only the checks of <see cref="ActAsync"/> that do not depend on the verb: no server, then the
    /// three off-limits accounts.
    /// </remarks>
    public async Task<DiscordMemberActionResult?> CheckAsync(string? userId, CancellationToken ct)
    {
        if (_facts is null || _partitions is null)
            return new(DiscordMemberActionStatus.CannotRecord, "This deployment cannot record actions.");

        if (string.IsNullOrWhiteSpace(userId))
            return new(DiscordMemberActionStatus.NoOne, "Say who, by their Discord id.");

        var guildId = await GuildIdAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(guildId))
            return new(DiscordMemberActionStatus.NoServer, "No Discord server is set up yet.");

        return await OffLimitsAsync(guildId, userId, ct).ConfigureAwait(false);
    }

    private async Task<string?> GuildIdAsync(CancellationToken ct)
        => await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// The refusal for an account that may not be acted on, or null when it may. Ids are compared as
    /// the number they spell, because Discord reads "0123" as 123 and a text comparison would let
    /// that through (the id is otherwise opaque, foundation §3.1.1).
    /// </summary>
    private async Task<DiscordMemberActionResult?> OffLimitsAsync(string guildId, string userId, CancellationToken ct)
    {
        // Not a number: nothing here can match it, and Discord's side says it is not an id.
        if (!ulong.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var target))
            return null;

        var off = await _discord.OffLimitsAsync(guildId, ct).ConfigureAwait(false);

        if (Spells(off.BotUserId, target))
            return new(DiscordMemberActionStatus.OffLimits, BotMessage);

        if (Spells(off.OwnerId, target))
            return new(DiscordMemberActionStatus.OffLimits, OwnerMessage);

        // A staff account's Discord id, proven or typed in: either way it names a moderator.
        var staff = await _db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.DiscordUserId != null && u.DiscordUserId != string.Empty)
            .Select(u => u.DiscordUserId!)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (staff.Any(id => Spells(id, target)))
            return new(DiscordMemberActionStatus.OffLimits, StaffMessage);

        return null;
    }

    private static bool Spells(string? id, ulong number)
        => id is not null
            && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed == number;

    /// <summary>What Discord's audit log shows: who asked, in Modbot, and why.</summary>
    public static string Reason(string verb, string by, string? why)
    {
        var text = $"Modbot: {verb} by {by}" + (string.IsNullOrWhiteSpace(why) ? string.Empty : $": {why.Trim()}");
        return text.Length <= MaxReasonLength ? text : text[..(MaxReasonLength - 1)] + "…";
    }
}
