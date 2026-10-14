using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Supporters;

/// <summary>What happened to a payment message.</summary>
/// <param name="Result"><see cref="SupporterIntake.Taken"/>, <see cref="SupporterIntake.Ignored"/> or <see cref="SupporterIntake.Repeat"/>.</param>
/// <param name="SupporterId">The supporter it was counted for, when it was taken.</param>
public sealed record SupporterIntakeOutcome(string Result, Guid? SupporterId);

/// <summary>
/// Turns a payment message from a site into a supporter row: who paid, which tier, and until when
/// the month is paid. It gives no role itself; the role job reads what this writes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only monthly payments count.</strong> A one-off gift, a shop order or a commission is
/// ignored, as is a payment with no tier name: there is nothing to give a role for.
/// </para>
/// <para>
/// <strong>The same message twice is one payment</strong> (the site may send it again when it did
/// not hear an answer). A payment older than the newest one already taken is written down but
/// changes nothing, so a late message never undoes a tier change.
/// </para>
/// </remarks>
public sealed class SupporterIntake(ModbotContext db)
{
    public const string Taken = "taken";
    public const string Ignored = "ignored";
    public const string Repeat = "repeat";

    private const int MostNameLength = 100;
    private const int MostCodeLength = 100;
    private const int MostDiscordIdLength = 40;

    /// <summary>Takes in one Ko-fi payment message. The caller has already checked the verification token.</summary>
    public async Task<SupporterIntakeOutcome> TakeKofiAsync(KofiPayment payment, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var tierName = payment.TierName?.Trim();
        var code = payment.TransactionId?.Trim();

        if (!payment.IsSubscriptionPayment
            || string.IsNullOrEmpty(tierName)
            || string.IsNullOrEmpty(code)
            || tierName.Length > SupporterLimits.MostTierNameLength
            || code.Length > MostCodeLength)
        {
            return new SupporterIntakeOutcome(Ignored, null);
        }

        if (await db.SupporterPayments.AnyAsync(p => p.Provider == SupporterProviders.Kofi && p.Code == code, ct).ConfigureAwait(false))
            return new SupporterIntakeOutcome(Repeat, null);

        var discordId = payment.DiscordUserId is { Length: > 0 } and { Length: <= MostDiscordIdLength } ? payment.DiscordUserId : null;
        var name = string.IsNullOrWhiteSpace(payment.FromName) ? "Ko-fi supporter" : Clip(payment.FromName.Trim(), MostNameLength);
        var payerId = SupporterRules.PayerIdOf(SupporterProviders.Kofi, payment.Email, discordId, name);
        var paidAt = payment.PaidAt() is { } stated && stated < now ? stated : now;

        var key = SupporterRules.TierKeyOf(tierName);
        var tier = await db.SupporterTiers.FirstOrDefaultAsync(t => t.Provider == SupporterProviders.Kofi && t.Key == key, ct).ConfigureAwait(false);

        if (tier is null)
        {
            // The first payment at a tier makes it, with no roles yet: staff pick them on the page.
            tier = new SupporterTier { Provider = SupporterProviders.Kofi, Key = key, Name = tierName, CreatedAt = now };
            db.SupporterTiers.Add(tier);
        }

        var supporter = await db.Supporters.FirstOrDefaultAsync(s => s.Provider == SupporterProviders.Kofi && s.PayerId == payerId, ct).ConfigureAwait(false);

        if (supporter is null && discordId is not null)
        {
            supporter = await db.Supporters
                .FirstOrDefaultAsync(s => s.Provider == SupporterProviders.Kofi && s.DiscordUserId == discordId, ct)
                .ConfigureAwait(false);
        }

        var isNew = supporter is null;

        if (supporter is null)
        {
            supporter = new Supporter
            {
                Provider = SupporterProviders.Kofi,
                PayerId = payerId,
                Name = name,
                State = SupporterStates.Active,
                FirstPaidAt = paidAt,
                LastPaidAt = DateTimeOffset.MinValue,
            };
            db.Supporters.Add(supporter);
        }

        var newest = paidAt >= supporter.LastPaidAt;

        if (newest)
        {
            var wasEnded = supporter.State == SupporterStates.Ended;
            var tierChanged = supporter.TierId != tier.Id;

            supporter.Name = name;
            supporter.TierId = tier.Id;
            supporter.LastPaidAt = paidAt;
            supporter.PaidThrough = SupporterRules.PaidThrough(paidAt);
            supporter.State = SupporterRules.HasLapsed(supporter.PaidThrough, now) ? SupporterStates.Ended : SupporterStates.Active;
            supporter.Problem = null;
            supporter.TryAgainAt = null;

            if (discordId is not null)
                supporter.DiscordUserId = discordId;

            // A new tier, or a comeback after the roles came off, is worth saying thank you for again.
            if (isNew || tierChanged || (wasEnded && supporter.State == SupporterStates.Active))
                supporter.ThanksSentAt = null;
        }

        if (paidAt < supporter.FirstPaidAt)
            supporter.FirstPaidAt = paidAt;

        supporter.UpdatedAt = now;

        db.SupporterPayments.Add(new SupporterPayment
        {
            Provider = SupporterProviders.Kofi,
            Code = code,
            SupporterId = supporter.Id,
            PaidAt = paidAt,
            ReceivedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The same message arrived twice at once and the other one won.
            db.ChangeTracker.Clear();
            return new SupporterIntakeOutcome(Repeat, null);
        }

        return new SupporterIntakeOutcome(Taken, supporter.Id);
    }

    private static string Clip(string text, int most) => text.Length <= most ? text : text[..most];
}
