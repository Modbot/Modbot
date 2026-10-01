using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Sends the direct messages for an event's invites: to the people VRChat would not take an invite
/// for, and to the people Modbot only knows on Discord (calendar auto-invite design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The message is the event's title and a Join button, nothing else.</strong> Sent with
/// mentions off, from the server's own bot.
/// </para>
/// <para>
/// <strong>Once.</strong> A row is marked messaging before the message goes out; a crash between
/// the two leaves it there, counted as sent and never sent again. A refusal is final too:
/// somebody with direct messages closed is not asked again.
/// </para>
/// <para>
/// Discord.Net keeps to Discord's own limits; this also waits <see cref="Between"/> between two
/// messages and sends at most <see cref="PerPass"/> a pass, so a long list goes out as a trickle
/// rather than a burst.
/// </para>
/// </remarks>
public sealed class CalendarInviteMessages
{
    /// <summary>The most direct messages one pass sends.</summary>
    public const int PerPass = 5;

    /// <summary>The pause between two direct messages.</summary>
    public static readonly TimeSpan Between = TimeSpan.FromSeconds(2);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarInvites _invites;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public CalendarInviteMessages(
        ModbotContext db,
        IModbotClock clock,
        CalendarInvites invites,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(invites);

        _db = db;
        _clock = clock;
        _invites = invites;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>The direct message's words.</summary>
    public static string Text(string title) => $"You're invited to **{title}**.";

    /// <returns>How many direct messages went out.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        await _invites.StopAsync(_clock.UtcNow, ct).ConfigureAwait(false);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        var guildId = settings.DiscordGuildId?.Trim();

        var sent = 0;
        var tried = 0;

        while (tried < PerPass)
        {
            var now = _clock.UtcNow;

            var row = await _db.CalendarInvites
                .Where(i => i.State == CalendarInviteStates.ToMessage)
                .OrderBy(i => i.QueuedAt)
                .ThenBy(i => i.EventId)
                .ThenBy(i => i.Position)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (row is null)
                break;

            if (await _invites.WhyStoppedAsync(row.EventId, row.OccurrenceStartsAt, now, ct).ConfigureAwait(false) is { } why)
            {
                Mark(row, CalendarInviteStates.Stopped, why, now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            // Banned from the server since the queue was written.
            if (guildId is { Length: > 0 }
                && await _db.DiscordBans.AsNoTracking()
                    .AnyAsync(b => b.GuildId == guildId && b.UserId == row.DiscordUserId && b.LiftedAt == null, ct).ConfigureAwait(false))
            {
                Mark(row, CalendarInviteStates.Skipped, CalendarInvites.BannedFromServer, now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            // Only to somebody who still wants event invites; stopping them stops the message too.
            if (!await _invites.StillWantedAsync(row, ct).ConfigureAwait(false))
            {
                Mark(row, CalendarInviteStates.NotAsked, CalendarInvites.DidNotAsk, now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            var opening = await _db.CalendarOpenings.AsNoTracking()
                .FirstAsync(o => o.EventId == row.EventId && o.OccurrenceStartsAt == row.OccurrenceStartsAt, ct).ConfigureAwait(false);

            var title = await _db.CalendarEvents.AsNoTracking()
                .Where(e => e.Id == row.EventId)
                .Select(e => e.Title)
                .FirstAsync(ct).ConfigureAwait(false);

            if (tried > 0)
                await _delay(Between, ct).ConfigureAwait(false);

            tried++;

            if (InstanceJoinLink.For(opening.Location) is not { } link)
            {
                // A message with no way in would only be noise.
                Mark(row, CalendarInviteStates.CouldNotReach, "The instance has no join link.", now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                await _invites.RecordAsync(row, title, sent: false, via: "discord", now, ct: ct).ConfigureAwait(false);
                continue;
            }

            // Written first: never twice.
            Mark(row, CalendarInviteStates.Messaging, row.Problem, now);
            row.MessagedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            var outcome = await gateway.SendDirectMessageAsync(
                row.DiscordUserId!, Text(title), [new DiscordLinkButton("Join", link)], ct).ConfigureAwait(false);

            if (outcome.Sent)
            {
                Mark(row, CalendarInviteStates.Messaged, problem: null, now);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                await _invites.RecordAsync(row, title, sent: true, via: "discord", now, ct: ct).ConfigureAwait(false);
                sent++;
                continue;
            }

            Mark(row, CalendarInviteStates.CouldNotReach, CalendarInvites.Short(outcome.Error ?? "Discord refused the message."), now);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await _invites.RecordAsync(row, title, sent: false, via: "discord", now, ct: ct).ConfigureAwait(false);

            _log.Information("A direct message for the event {EventId} was refused", row.EventId);
        }

        return sent;
    }

    private static void Mark(CalendarInvite row, string state, string? problem, DateTimeOffset now)
    {
        row.State = state;
        row.Problem = problem;
        row.UpdatedAt = now;
    }
}
