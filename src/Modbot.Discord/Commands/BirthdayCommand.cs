using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/birthday</c>: a member sets, shows or removes their birthday, a month and a day and never a
/// year.
/// </summary>
/// <remarks>
/// <para>
/// For every member, with no Modbot account, and private. The year is not asked for, so Modbot
/// holds no age. The daily post and the birthday role are the API's pass
/// (<c>BirthdayPass</c>); this only keeps the member's own row.
/// </para>
/// <para>
/// Taking a birthday off also takes the birthday role off the member at once when Modbot gave it
/// them today, so no one is left wearing it.
/// </para>
/// </remarks>
public sealed class BirthdayCommand
{
    /// <summary>The key of the per-person limit, apart from the other commands'.</summary>
    public const string LimitsKey = "birthday";

    public const string TooFastMessage = "Slow down. Try again in a minute.";
    public const string NotADateMessage = "That date does not exist.";
    public const string NoneMessage = "You have not set a birthday.";
    public const string RemovedMessage = "Your birthday is removed.";
    public const string NothingToRemoveMessage = "You have not set a birthday, so there is nothing to remove.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public BirthdayCommand(
        ModbotContext db,
        IModbotClock clock,
        [FromKeyedServices(LimitsKey)] MemberCommandLimits limits)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _clock = clock;
        _limits = limits;
    }

    /// <summary>Counts one use for this person and says whether it is allowed. See <see cref="MemberCommandLimits"/>.</summary>
    public bool TryUse(string discordUserId) => _limits.TryUse(discordUserId, _clock.UtcNow);

    /// <summary>"14 March": the day and the month's name, in English.</summary>
    public static string Say(int month, int day)
        => $"{day.ToString(CultureInfo.InvariantCulture)} {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)}";

    /// <param name="gateway">The session, to take the birthday role off a member who removes their birthday.</param>
    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var row = await _db.MemberBirthdays
            .FirstOrDefaultAsync(b => b.DiscordUserId == call.DiscordUserId, ct)
            .ConfigureAwait(false);

        switch (call.Subcommand)
        {
            case DiscordCommands.BirthdaySet:
                return await SetAsync(call, row, ct).ConfigureAwait(false);

            case DiscordCommands.BirthdayRemove:
                return await RemoveAsync(call, row is { IsForgotten: false } ? row : null, gateway, ct).ConfigureAwait(false);

            default:
                return row is not { IsForgotten: false }
                    ? DiscordReply.Say(NoneMessage)
                    : DiscordReply.Say($"Your birthday is {Say(row.Month, row.Day)}.");
        }
    }

    private async Task<DiscordReply> SetAsync(DiscordCommandCall call, MemberBirthday? row, CancellationToken ct)
    {
        if (!int.TryParse(call.Option(DiscordCommands.BirthdayMonthOption), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(call.Option(DiscordCommands.BirthdayDayOption), NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !BirthdayRules.IsDate(month, day))
        {
            return DiscordReply.Say(NotADateMessage);
        }

        if (row is null)
        {
            _db.MemberBirthdays.Add(new MemberBirthday
            {
                DiscordUserId = call.DiscordUserId,
                Month = month,
                Day = day,
                SetAt = _clock.UtcNow,
            });
        }
        else
        {
            row.Month = month;
            row.Day = day;
            row.SetAt = _clock.UtcNow;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DiscordReply.Say($"Your birthday is set to {Say(month, day)}.");
    }

    private async Task<DiscordReply> RemoveAsync(DiscordCommandCall call, MemberBirthday? row, IDiscordGateway? gateway, CancellationToken ct)
    {
        if (row is null)
            return DiscordReply.Say(NothingToRemoveMessage);

        // A role Modbot gave for today comes off with the birthday. If it cannot come off now, it is
        // kept on the row and the next pass takes it off, so only a role that is gone is forgotten.
        var roleStillOn = row.RoleGivenId is { } roleId
            && !await TakeRoleAsync(gateway, roleId, call.DiscordUserId, ct).ConfigureAwait(false);

        if (roleStillOn)
            row.Forget();
        else
            _db.MemberBirthdays.Remove(row);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DiscordReply.Say(RemovedMessage);
    }

    /// <summary>
    /// Takes the role off. True when it is off (taken, or the member or role is gone); false when it
    /// could not be tried or Discord refused, so the next pass must.
    /// </summary>
    private async Task<bool> TakeRoleAsync(IDiscordGateway? gateway, string roleId, string userId, CancellationToken ct)
    {
        if (gateway is null)
            return false;

        var guildId = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (guildId is null)
            return false;

        var outcome = await gateway
            .ChangeRoleAsync(guildId, userId, roleId, add: false, "Modbot: birthday removed", ct)
            .ConfigureAwait(false);

        return outcome.Done || outcome.NotInServer || outcome.RoleGone;
    }
}
