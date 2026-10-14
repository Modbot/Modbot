using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Supporters;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/supporter-code</c>: the short code a Ko-fi supporter pastes into Ko-fi's message box when they
/// pay, so Modbot can match the payment to their Discord account (<see cref="SupporterCodes"/>).
/// </summary>
/// <remarks>
/// For every member, with no Modbot account, and private: only the person who asked sees the code.
/// Asking again gives the same code. It is registered only while Ko-fi supporters is on
/// (<see cref="DiscordCommands.For"/>); a run typed after it was turned off is told so.
/// </remarks>
public sealed class SupporterCodeCommand
{
    /// <summary>The key of the per-person limit, apart from the other commands'.</summary>
    public const string LimitsKey = "supporter-code";

    public const string TooFastMessage = "Slow down. Try again in a minute.";

    public const string OffMessage = "Ko-fi supporters is turned off on this server.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public SupporterCodeCommand(
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

    /// <summary>The answer for a code: the code, and what to do with it.</summary>
    public static string Answer(string code)
        => $"Your supporter code is **{code}**. Paste it into the message box on Ko-fi when you pay, so Modbot can match your payment to this Discord account. Use the same code every time.";

    /// <summary>Runs <c>/supporter-code</c>.</summary>
    public async Task<DiscordReply> RunAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_limits.TryUse(call.DiscordUserId, _clock.UtcNow))
            return DiscordReply.Say(TooFastMessage);

        var on = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.KofiOn)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!on)
            return DiscordReply.Say(OffMessage);

        var code = await new SupporterCodes(_db).ForAsync(call.DiscordUserId, _clock.UtcNow, ct).ConfigureAwait(false);
        return DiscordReply.Say(Answer(code));
    }
}
