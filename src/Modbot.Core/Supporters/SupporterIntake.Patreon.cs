using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Supporters;

/// <summary>
/// The Patreon half of <see cref="SupporterIntake"/>. Patreon sends a message when a membership is
/// made, changed or deleted, and again when a charge goes through or fails.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The status decides, not the kind of message.</strong> An active patron has the tier's roles
/// for the month their last paid charge covers (or a month from now, until Patreon charges them). A
/// patron whose charge was declined keeps them until three days after the declined charge. A patron
/// whose pledge ended keeps them until the paid month is up. Each is then given the same three days
/// as a Ko-fi supporter ends with (<see cref="SupporterRules.RolesEndAt"/>). A later message that says
/// active gives the roles back.
/// </para>
/// <para>
/// <strong>The same message twice changes nothing.</strong> A charge is written down once, by the
/// member and the charge date; the supporter is brought to what the message says either way, so a
/// pledge deleted on the charge's own date is not mistaken for a repeat of the charge.
/// </para>
/// <para>
/// Patreon's message names no Discord account, so a new patron waits to be matched by staff.
/// </para>
/// </remarks>
public sealed partial class SupporterIntake
{
    private const int MostMemberIdLength = 64;

    /// <summary>Takes in one Patreon membership message. The caller has already checked its signature.</summary>
    /// <param name="message">The message.</param>
    /// <param name="trigger">Patreon's name for what happened, such as <c>members:pledge:create</c>; may be null.</param>
    /// <param name="now">The moment it came in.</param>
    /// <param name="ct">Cancels the work.</param>
    public Task<SupporterIntakeOutcome> TakePatreonAsync(PatreonMessage message, string? trigger, DateTimeOffset now, CancellationToken ct = default)
        => TakePatreonAsync(message, trigger, now, true, ct);

    private async Task<SupporterIntakeOutcome> TakePatreonAsync(PatreonMessage message, string? trigger, DateTimeOffset now, bool mayRetry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var memberId = message.MemberId;
        var standing = PatreonRules.StandingOf(trigger, message.PatronStatus);

        if (memberId is null || memberId.Length > MostMemberIdLength || standing == PatreonStanding.None)
            return new SupporterIntakeOutcome(Ignored, null);

        var payerId = PatreonRules.PayerIdOf(memberId);
        var supporter = await db.Supporters
            .FirstOrDefaultAsync(s => s.Provider == SupporterProviders.Patreon && s.PayerId == payerId, ct)
            .ConfigureAwait(false);

        var tierPick = message.EntitledTier();

        // A patron entitled to no tier has nothing to be given: they are treated as one whose pledge ended.
        if (standing == PatreonStanding.Active && tierPick is null)
            standing = PatreonStanding.Ended;

        if (tierPick is { Id.Length: > SupporterLimits.MostTierNameLength })
            return new SupporterIntakeOutcome(Ignored, null);

        // Somebody who never counted as a supporter has nothing to take away, and a declined or ended
        // message does not say which tier they were at.
        if (supporter is null && standing != PatreonStanding.Active)
            return new SupporterIntakeOutcome(Ignored, null);

        var name = string.IsNullOrWhiteSpace(message.FullName) ? "Patreon supporter" : Clip(message.FullName.Trim(), MostNameLength);
        var charged = message.LastPaidCharge is { } paid ? (paid < now ? paid : now) : (DateTimeOffset?)null;
        var isNew = supporter is null;

        if (standing == PatreonStanding.Active)
        {
            var tier = await FindOrMakeTierAsync(tierPick!, now, ct).ConfigureAwait(false);

            if (supporter is null)
            {
                supporter = new Supporter
                {
                    Provider = SupporterProviders.Patreon,
                    PayerId = payerId,
                    Name = name,
                    State = SupporterStates.Active,
                    FirstPaidAt = charged ?? now,
                    LastPaidAt = charged ?? now,
                    PaidThrough = DateTimeOffset.MinValue,
                };
                db.Supporters.Add(supporter);
            }

            // The month the last paid charge covers. With no paid charge yet (the first one is still to
            // come) the supporter keeps what they have, or else gets a month from now. Patreon says they
            // are an active patron, so a month already over is not believed: it starts again from now.
            var through = charged is { } c
                ? SupporterRules.PaidThrough(c)
                : !isNew && !SupporterRules.HasLapsed(supporter.PaidThrough, now)
                    ? supporter.PaidThrough
                    : SupporterRules.PaidThrough(now);

            if (SupporterRules.HasLapsed(through, now))
                through = SupporterRules.PaidThrough(now);

            var wasEnded = supporter.State == SupporterStates.Ended;
            var tierChanged = supporter.TierId != tier.Id;

            supporter.Name = name;
            supporter.TierId = tier.Id;
            supporter.PaidThrough = through;
            supporter.State = SupporterStates.Active;

            if (charged is { } chargedAt)
            {
                if (chargedAt >= supporter.LastPaidAt || isNew)
                    supporter.LastPaidAt = chargedAt;

                if (chargedAt < supporter.FirstPaidAt)
                    supporter.FirstPaidAt = chargedAt;
            }

            // A new tier, or a comeback after the roles came off, is worth saying thank you for again.
            if (isNew || tierChanged || wasEnded)
            {
                supporter.ThanksSentAt = null;
                supporter.Problem = null;
                supporter.TryAgainAt = null;
            }
        }
        else
        {
            // A declined charge ends the paid time where the charge was tried, so the roles go three
            // days after it. Somebody whose roles already came off is not given more time by it. A
            // pledge that ended is paid for the month its last paid charge covers; with no paid charge
            // behind it there is no month to wait out.
            if (standing == PatreonStanding.Declined)
            {
                if (supporter!.State == SupporterStates.Active)
                    supporter.PaidThrough = message.LastChargeDate is { } tried && tried < now ? tried : now;
            }
            else if (charged is { } lastPaid)
            {
                supporter!.PaidThrough = SupporterRules.PaidThrough(lastPaid);
            }
            else if (now < supporter!.PaidThrough)
            {
                supporter.PaidThrough = now;
            }

            if (supporter.State == SupporterStates.Active && SupporterRules.HasLapsed(supporter.PaidThrough, now))
                supporter.State = SupporterStates.Ended;
        }

        // A charge is written down once; the supporter was brought up to date above either way.
        if (charged is { } chargeDate)
        {
            var code = PatreonRules.PaymentCode(memberId, chargeDate);

            if (!await db.SupporterPayments.AnyAsync(p => p.Provider == SupporterProviders.Patreon && p.Code == code, ct).ConfigureAwait(false))
            {
                db.SupporterPayments.Add(new SupporterPayment
                {
                    Provider = SupporterProviders.Patreon,
                    Code = code,
                    SupporterId = supporter.Id,
                    PaidAt = chargeDate,
                    ReceivedAt = now,
                });
            }
        }

        // A message that changes nothing (the same one again) is a repeat and leaves the row alone.
        var changed = db.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified);

        if (changed)
            supporter.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            // The same message arrived twice at once, or another one made the same new patron or tier
            // first: read what it wrote and apply this one on top. Patreon was answered and will not resend it.
            if (mayRetry)
                return await TakePatreonAsync(message, trigger, now, false, ct).ConfigureAwait(false);

            throw;
        }

        return changed ? new SupporterIntakeOutcome(Taken, supporter.Id) : new SupporterIntakeOutcome(Repeat, null);
    }

    /// <summary>The tier Patreon named, made (with no roles yet) the first time and renamed if Patreon renamed it.</summary>
    private async Task<SupporterTier> FindOrMakeTierAsync(PatreonTier pick, DateTimeOffset now, CancellationToken ct)
    {
        var title = pick.Title is { } t ? Clip(t, SupporterLimits.MostTierNameLength) : null;

        var tier = await db.SupporterTiers
            .FirstOrDefaultAsync(t => t.Provider == SupporterProviders.Patreon && t.Key == pick.Id, ct)
            .ConfigureAwait(false);

        if (tier is null)
        {
            tier = new SupporterTier
            {
                Provider = SupporterProviders.Patreon,
                Key = pick.Id,
                Name = title ?? "Patreon tier",
                CreatedAt = now,
            };
            db.SupporterTiers.Add(tier);
        }
        else if (title is not null && tier.Name != title)
        {
            tier.Name = title;
        }

        return tier;
    }
}
