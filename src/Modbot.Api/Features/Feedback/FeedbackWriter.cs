using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Feedback;

/// <summary>
/// Saves one feedback item as the person who sent it, and records that they did.
/// </summary>
/// <remarks>
/// <para>
/// The one place feedback is written, so the companion's Send and the chat tool that files feedback
/// (feedback design §7) check the same things and leave the same record. The item goes into this
/// server's own table and nowhere else (feedback design §1).
/// </para>
/// <para>
/// The fact names the kind and nothing else: the title and the details stay in the table, so deleting
/// an item leaves no copy of its words in the log.
/// </para>
/// </remarks>
public sealed class FeedbackWriter(ModbotContext db, AccountFacts facts, IModbotClock clock)
{
    /// <summary>What a request holds once it has been checked.</summary>
    public sealed record Checked(string Kind, string Title, string Details);

    /// <summary>
    /// Trims the request and checks it. Returns what is wrong in <paramref name="problem"/>, or the
    /// cleaned request. A missing title is the first line of the details, cut to fit.
    /// </summary>
    public static Checked? Check(string? kind, string? title, string? details, out string? problem)
    {
        var cleanKind = kind?.Trim().ToLowerInvariant();
        if (cleanKind is null || !FeedbackKinds.All.Contains(cleanKind))
        {
            problem = "The kind must be bug, idea or change.";
            return null;
        }

        var cleanDetails = details?.Trim();
        if (string.IsNullOrEmpty(cleanDetails))
        {
            problem = "Say what it is about.";
            return null;
        }

        if (cleanDetails.Length > FeedbackItem.MaxDetailsLength)
        {
            problem = $"The details can be at most {FeedbackItem.MaxDetailsLength} characters.";
            return null;
        }

        var cleanTitle = title?.Trim();
        if (string.IsNullOrEmpty(cleanTitle))
        {
            var firstLine = cleanDetails.Split('\n', 2)[0].Trim();
            cleanTitle = firstLine.Length <= FeedbackItem.MaxTitleLength
                ? firstLine
                : string.Concat(firstLine.AsSpan(0, FeedbackItem.MaxTitleLength - 1), "…");
        }

        if (cleanTitle.Length > FeedbackItem.MaxTitleLength)
        {
            problem = $"The title can be at most {FeedbackItem.MaxTitleLength} characters.";
            return null;
        }

        problem = null;
        return new Checked(cleanKind, cleanTitle, cleanDetails);
    }

    /// <summary>Saves an item that has already passed <see cref="Check"/>.</summary>
    public async Task<FeedbackItem> SaveAsync(Actor sender, Checked words, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(words);

        var item = new FeedbackItem
        {
            Id = Guid.CreateVersion7(),
            SenderUserId = sender.Id,
            SenderName = sender.Username,
            Kind = words.Kind,
            Title = words.Title,
            Details = words.Details,
            Status = FeedbackStatuses.New,
            CreatedAt = clock.UtcNow,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Feedback.Add(item);
        await db.SaveChangesAsync(ct);

        await facts.RecordAsync(
            FactType.FeedbackSent, item.Id.ToString(), sender,
            new JsonObject { ["kind"] = item.Kind }, ct);

        await transaction.CommitAsync(ct);

        return item;
    }
}
