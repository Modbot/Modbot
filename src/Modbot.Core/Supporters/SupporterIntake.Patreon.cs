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
/// patron whose charge was declined keeps them for the days Settings → Supporters gives after the
/// declined charge. A patron whose pledge ended keeps them until the paid month is up. Each is then
/// given those same days (<see cref="SupporterRules.RolesEndAt"/>), or keeps the roles for good when
/// the setting is never. A later message that says active gives the roles back.
/// </para>
/// <para>
/// <strong>A late message does not undo a newer one.</strong> Patreon sends no event id, so an active
/// message whose last charge is older than the one already taken is written down but changes nothing,
/// as with a late Ko-fi payment.
/// </para>
/// <para>
/// <strong>The same message twice changes nothing.</strong> A charge is written down once, by the
/// member and the charge date; the supporter is brought to what the message says either way, so a
/// pledge deleted on the charge's own date is not mistaken for a repeat of the charge.
/// </para>
/// <para>
/// A webhook message names no Discord account, so a new patron waits for the lookup that asks Patreon
/// (<see cref="PatreonCatchUp"/>) or for staff. A message that carries the user's connected apps names it, and
/// the account is matched with Ko-fi's rules: one another supporter already has stays unmatched for staff.
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
    /// <param name="discordKnown">
    /// The message was fetched from Patreon with the user's connected apps, so it already says whether the patron has a
    /// Discord account and asking Patreon again would add nothing.
    /// </param>
    public Task<SupporterIntakeOutcome> TakePatreonAsync(PatreonMessage message, string? trigger, DateTimeOffset now, CancellationToken ct = default, bool discordKnown = false)
        => TakePatreonAsync(message, trigger, now, discordKnown, true, ct);

    private async Task<SupporterIntakeOutcome> TakePatreonAsync(PatreonMessage message, string? trigger, DateTimeOffset now, bool discordKnown, bool mayRetry, CancellationToken ct)
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
        var offDays = await RolesOffDaysAsync(SupporterProviders.Patreon, ct).ConfigureAwait(false);

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

        // A message that says active but whose last charge is older than the one already taken is a late or
        // replayed one: it must neither extend the month nor bring back roles that came off. Only its charge
        // is written down, below.
        // It is judged against the newest charge written down, not against the supporter's last-paid time,
        // which for a patron with no charge yet is only the moment they came in.
        DateTimeOffset? newestCharge = null;

        if (standing == PatreonStanding.Active && supporter is not null && charged is not null)
        {
            var supporterId = supporter.Id;
            newestCharge = await db.SupporterPayments
                .Where(p => p.SupporterId == supporterId)
                .MaxAsync(p => (DateTimeOffset?)p.PaidAt, ct)
                .ConfigureAwait(false);
        }

        var late = charged is { } lateCharge && newestCharge is { } newest && lateCharge < newest;

        if (standing == PatreonStanding.Active && !late)
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
                    PatreonMemberId = memberId,
                };
                db.Supporters.Add(supporter);
            }

            // The month the last paid charge covers. With no paid charge yet (the first one is still to
            // come) the supporter keeps what they have, or else gets a month from now. Patreon says they
            // are an active patron, so a month already over is not believed: it starts again from now.
            var through = charged is { } c
                ? SupporterRules.PaidThrough(c)
                : !isNew && !SupporterRules.HasLapsed(supporter.PaidThrough, now, offDays)
                    ? supporter.PaidThrough
                    : SupporterRules.PaidThrough(now);

            if (SupporterRules.HasLapsed(through, now, offDays))
                through = SupporterRules.PaidThrough(now);

            var wasEnded = supporter.State == SupporterStates.Ended;
            var tierChanged = supporter.TierId != tier.Id;

            supporter.Name = name;
            supporter.TierId = tier.Id;
            supporter.PaidThrough = through;
            supporter.State = SupporterStates.Active;

            if (charged is { } chargedAt)
            {
                if (isNew || newestCharge is null || chargedAt >= newestCharge)
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
        else if (standing != PatreonStanding.Active)
        {
            // A declined charge ends the paid time where the charge was tried, so the roles go the
            // set number of days after it. Somebody whose roles already came off is not given more time by it. A
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

            if (supporter.State == SupporterStates.Active && SupporterRules.HasLapsed(supporter.PaidThrough, now, offDays))
                supporter.State = SupporterStates.Ended;
        }

        // Every way through the above leaves a supporter: a new patron was made, anybody else was found.
        ArgumentNullException.ThrowIfNull(supporter);

        supporter.PatreonMemberId ??= memberId;

        // Patreon names the account the patron connected, as Ko-fi's link does, and is matched the same way:
        // an account another supporter already has stays with them, and the patron waits for staff.
        if (!late)
        {
            if (message.DiscordUserId is { Length: > 0 and <= MostDiscordIdLength } discordId
                && supporter.DiscordUserId != discordId
                && !await db.Supporters.AnyAsync(s => s.Id != supporter.Id && s.Provider == SupporterProviders.Patreon && s.DiscordUserId == discordId, ct).ConfigureAwait(false))
            {
                // The role Modbot gave the old account is taken back from it, so remember whose it was.
                if (supporter.GivenDiscordRoleId is not null && supporter.DiscordUserId is not null)
                    supporter.GivenDiscordUserId ??= supporter.DiscordUserId;

                supporter.DiscordUserId = discordId;
            }
        }

        if (discordKnown)
            supporter.DiscordLookupAt ??= now;

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
                return await TakePatreonAsync(message, trigger, now, discordKnown, false, ct).ConfigureAwait(false);

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
