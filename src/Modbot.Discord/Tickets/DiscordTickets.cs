using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Tickets;

/// <summary>
/// What the buttons of member tickets do: a member opens a private thread with staff, and the
/// member or staff closes it with a transcript for the mod log, or staff delete it.
/// </summary>
/// <remarks>
/// Every answer is private to the one who pressed. A ticket is closed or deleted by the thread it is
/// pressed in, never by an id in the button, so a button copied from one ticket cannot touch another.
/// </remarks>
public sealed class DiscordTickets
{
    public const string OpenPrefix = DiscordActionButton.Prefix + "ticket:open:";
    public const string CloseId = DiscordActionButton.Prefix + "ticket:close";
    public const string DeleteId = DiscordActionButton.Prefix + "ticket:delete";

    private const string TicketPrefix = DiscordActionButton.Prefix + "ticket:";

    /// <summary>Discord ends a thread's name at 100 characters.</summary>
    public const int MostThreadNameLength = 100;

    /// <summary>The pages of 100 messages read for a transcript at most: 5,000 messages.</summary>
    public const int MostPages = 50;

    public const string NoOpenTicketMessage = "This ticket is already closed.";
    public const string NotYoursMessage = "Only the member who opened this ticket, or staff, can close it.";
    public const string StaffOnlyMessage = "Only staff can delete a ticket.";
    public const string OfflineMessage = "The bot is not connected. Try again in a moment.";
    public const string GoneTypeMessage = "That kind of ticket is not offered any more.";
    public const string NoPlaceMessage = "A ticket cannot be opened from here.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public DiscordTickets(ModbotContext db, IModbotClock clock, IFactWriter facts, EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
    }

    public static bool IsButton(string buttonId) => buttonId.StartsWith(TicketPrefix, StringComparison.Ordinal);

    /// <summary>The button on the panel that opens a ticket of one type.</summary>
    public static string OpenButtonId(Guid typeId) => OpenPrefix + typeId.ToString("D");

    /// <param name="messageTextOn">Discord sends message text; false when the Message Content intent is off.</param>
    public async Task<DiscordReply> HandleButtonAsync(
        IDiscordGateway? gateway, DiscordButtonPress press, bool messageTextOn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (gateway is null)
            return DiscordReply.Say(OfflineMessage);

        if (press.ButtonId.StartsWith(OpenPrefix, StringComparison.Ordinal))
        {
            return Guid.TryParseExact(press.ButtonId[OpenPrefix.Length..], "D", out var typeId)
                ? await OpenAsync(gateway, press, typeId, ct).ConfigureAwait(false)
                : DiscordReply.Say(GoneTypeMessage);
        }

        return press.ButtonId switch
        {
            CloseId => await CloseAsync(gateway, press, messageTextOn, ct).ConfigureAwait(false),
            DeleteId => await DeleteAsync(gateway, press, ct).ConfigureAwait(false),
            _ => DiscordReply.Say(NoOpenTicketMessage),
        };
    }

    // ── Open ─────────────────────────────────────────────────────────────────────────────────

    private async Task<DiscordReply> OpenAsync(IDiscordGateway gateway, DiscordButtonPress press, Guid typeId, CancellationToken ct)
    {
        var type = await _db.DiscordTicketTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == typeId, ct)
            .ConfigureAwait(false);

        if (type is null)
            return DiscordReply.Say(GoneTypeMessage);

        if (press.CardChannelId is not { } channelId)
            return DiscordReply.Say(NoPlaceMessage);

        var held = await _db.DiscordTickets
            .FirstOrDefaultAsync(
                t => t.TypeId == typeId && t.DiscordUserId == press.DiscordUserId && t.State == DiscordTicketStates.Open,
                ct)
            .ConfigureAwait(false);

        if (held is not null)
        {
            // A thread staff deleted by hand leaves the row open: when Discord says it is gone,
            // the member may open a new one. When it cannot be asked, the member is sent to it.
            if (await gateway.TicketThreadExistsAsync(held.ThreadId, ct).ConfigureAwait(false) is not false)
                return DiscordReply.Say($"You already have an open ticket: {ThreadLink(held.ThreadId)}");

            held.State = DiscordTicketStates.Deleted;
            held.DeletedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var opening = new DiscordTicketOpening(
            channelId,
            ThreadName(type.Name, press.DiscordUsername),
            press.DiscordUserId,
            type.StaffRoleId,
            type.OpeningMessage,
            [
                new DiscordActionButton("Close", CloseId),
                new DiscordActionButton("Delete", DeleteId, DiscordButtonStyle.Danger),
            ]);

        var outcome = await gateway.OpenTicketThreadAsync(opening, ct).ConfigureAwait(false);
        if (!outcome.Opened || outcome.ThreadId is not { } threadId)
            return DiscordReply.Say(outcome.Error ?? "The ticket could not be opened.");

        var ticket = new DiscordTicket
        {
            TypeId = type.Id,
            TypeName = type.Name,
            DiscordUserId = press.DiscordUserId,
            DiscordUserName = press.DiscordUsername,
            ThreadId = threadId,
            ChannelId = channelId,
            State = DiscordTicketStates.Open,
            HoldsPrivateImages = type.HoldsPrivateImages,
            OpenedAt = _clock.UtcNow,
        };

        _db.DiscordTickets.Add(ticket);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two presses at once: the database keeps one open ticket of a type for a member.
            _db.Entry(ticket).State = EntityState.Detached;
            await gateway.DeleteTicketThreadAsync(threadId, "Modbot: the member already had this ticket open.", ct).ConfigureAwait(false);
            return DiscordReply.Say("You already have an open ticket.");
        }

        // The thread and its first message existed before this row did, and an image posted in that
        // gap was stored with its attachments. Take them off now; from here the message store does it.
        if (ticket.HoldsPrivateImages)
        {
            await _db.DiscordMessages
                .Where(m => m.ThreadId == threadId && m.Attachments != "[]")
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Attachments, "[]"), ct)
                .ConfigureAwait(false);
        }

        await RecordAsync(
                FactType.DiscordTicketOpened,
                ticket,
                press.DiscordUserId,
                new JsonObject
                {
                    ["ticketId"] = ticket.Id.ToString("D"),
                    ["type"] = ticket.TypeName,
                    ["threadId"] = threadId,
                    ["channelId"] = channelId,
                },
                ct)
            .ConfigureAwait(false);

        return DiscordReply.Say($"Your ticket is open: {ThreadLink(threadId)}");
    }

    // ── Close ────────────────────────────────────────────────────────────────────────────────

    private async Task<DiscordReply> CloseAsync(
        IDiscordGateway gateway, DiscordButtonPress press, bool messageTextOn, CancellationToken ct)
    {
        var ticket = await FindAsync(press, DiscordTicketStates.Open, ct).ConfigureAwait(false);
        if (ticket is null)
            return DiscordReply.Say(NoOpenTicketMessage);

        if (press.DiscordUserId != ticket.DiscordUserId && !await IsStaffAsync(ticket, press, ct).ConfigureAwait(false))
            return DiscordReply.Say(NotYoursMessage);

        var now = _clock.UtcNow;

        var transcriptChannel = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordTicketTranscriptChannelId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var posted = false;
        var messageCount = 0;

        if (!string.IsNullOrWhiteSpace(transcriptChannel))
        {
            var read = await ReadAllAsync(gateway, ticket.ThreadId, ct).ConfigureAwait(false);
            if (read.Error is not null)
                return DiscordReply.Say($"The ticket was not closed, because its messages could not be read: {read.Error}");

            messageCount = read.Messages.Count;

            var text = TicketTranscript.Build(
                ticket.TypeName,
                ticket.DiscordUserId,
                ticket.DiscordUserName,
                ticket.OpenedAt,
                press.DiscordUserId,
                now,
                read.Messages,
                ticket.HoldsPrivateImages,
                textMissing: !messageTextOn,
                olderLeftOut: read.OlderLeftOut);

            var file = new DiscordPicture($"ticket-{ticket.Id.ToString("N")[..8]}.txt", Encoding.UTF8.GetBytes(text));

            var card = new DiscordEmbedContent(
                Title: $"Ticket closed: {CardText.Fit(CardText.EscapeText(ticket.TypeName), 200)}",
                Description: $"Opened by <@{ticket.DiscordUserId}>. Closed by <@{press.DiscordUserId}>.",
                Color: CardColour.Violet,
                Fields: [new DiscordEmbedField("Messages", messageCount.ToString(CultureInfo.InvariantCulture), Inline: true)],
                Timestamp: now,
                Url: null,
                Footer: null);

            var sent = await gateway.PostAsync(transcriptChannel, null, [card], null, [file], ct).ConfigureAwait(false);
            if (!sent.Sent)
                return DiscordReply.Say($"The ticket was not closed, because the transcript could not be posted: {sent.Error}");

            posted = true;
        }

        var closed = await gateway.CloseTicketThreadAsync(ticket.ThreadId, $"Closed by <@{press.DiscordUserId}>.", ct).ConfigureAwait(false);
        if (!closed.Sent)
            return DiscordReply.Say($"The ticket could not be closed: {closed.Error}");

        ticket.State = DiscordTicketStates.Closed;
        ticket.ClosedAt = now;
        ticket.ClosedByDiscordUserId = press.DiscordUserId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordAsync(
                FactType.DiscordTicketClosed,
                ticket,
                press.DiscordUserId,
                new JsonObject
                {
                    ["ticketId"] = ticket.Id.ToString("D"),
                    ["type"] = ticket.TypeName,
                    ["threadId"] = ticket.ThreadId,
                    ["transcript"] = posted ? "posted" : "none",
                    ["messages"] = messageCount,
                },
                ct)
            .ConfigureAwait(false);

        return DiscordReply.Say(posted
            ? $"Ticket closed. The transcript is in <#{transcriptChannel}>."
            : "Ticket closed. No transcript was kept.");
    }

    private static async Task<(IReadOnlyList<DiscordMessageSnapshot> Messages, string? Error, bool OlderLeftOut)> ReadAllAsync(
        IDiscordGateway gateway, string threadId, CancellationToken ct)
    {
        var all = new List<DiscordMessageSnapshot>();
        string? before = null;
        var olderLeftOut = false;

        for (var page = 0; page < MostPages; page++)
        {
            var got = await gateway.ReadMessagesAsync(threadId, before, null, ct).ConfigureAwait(false);
            if (got.Error is not null)
                return ([], got.Error, false);

            all.AddRange(got.Messages);

            if (!got.Full || got.OldestId is null)
                break;

            before = got.OldestId;
            olderLeftOut = page == MostPages - 1;
        }

        return ([.. all.OrderBy(m => m.SentAt).ThenBy(m => m.Id, StringComparer.Ordinal)], null, olderLeftOut);
    }

    // ── Delete ───────────────────────────────────────────────────────────────────────────────

    private async Task<DiscordReply> DeleteAsync(IDiscordGateway gateway, DiscordButtonPress press, CancellationToken ct)
    {
        var ticket = await FindAsync(press, null, ct).ConfigureAwait(false);
        if (ticket is null || ticket.State == DiscordTicketStates.Deleted)
            return DiscordReply.Say(NoOpenTicketMessage);

        if (!await IsStaffAsync(ticket, press, ct).ConfigureAwait(false))
            return DiscordReply.Say(StaffOnlyMessage);

        var gone = await gateway.DeleteTicketThreadAsync(ticket.ThreadId, $"Modbot: deleted by {press.DiscordUsername}.", ct)
            .ConfigureAwait(false);

        // A thread that is already gone is what was asked for; any other failure leaves it as it was.
        if (!gone.Sent && !gone.NotFound)
            return DiscordReply.Say($"The ticket could not be deleted: {gone.Error}");

        ticket.State = DiscordTicketStates.Deleted;
        ticket.DeletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordAsync(
                FactType.DiscordTicketDeleted,
                ticket,
                press.DiscordUserId,
                new JsonObject
                {
                    ["ticketId"] = ticket.Id.ToString("D"),
                    ["type"] = ticket.TypeName,
                    ["threadId"] = ticket.ThreadId,
                },
                ct)
            .ConfigureAwait(false);

        return DiscordReply.Say("Ticket deleted.");
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The ticket whose thread the button was pressed in, in the state asked for or any live state.</summary>
    private Task<DiscordTicket?> FindAsync(DiscordButtonPress press, string? state, CancellationToken ct)
    {
        var threadId = press.CardChannelId;
        if (threadId is null)
            return Task.FromResult<DiscordTicket?>(null);

        return _db.DiscordTickets
            .FirstOrDefaultAsync(
                t => t.ThreadId == threadId && (state == null ? t.State != DiscordTicketStates.Deleted : t.State == state),
                ct);
    }

    /// <summary>Holds the type's staff role, or may manage threads. A type that was removed leaves only the second.</summary>
    private async Task<bool> IsStaffAsync(DiscordTicket ticket, DiscordButtonPress press, CancellationToken ct)
    {
        if (press.MemberCanManageThreads)
            return true;

        var role = await _db.DiscordTicketTypes.AsNoTracking()
            .Where(t => t.Id == ticket.TypeId)
            .Select(t => t.StaffRoleId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return !string.IsNullOrEmpty(role) && press.MemberRoleIds.Contains(role, StringComparer.Ordinal);
    }

    private async Task RecordAsync(string type, DiscordTicket ticket, string actorId, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = ticket.DiscordUserId,
                ActorPlatform = FactPlatform.Discord,
                ActorId = actorId,
                Source = FactSource.Modbot,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }

    private static string ThreadLink(string threadId) => $"<#{threadId}>";

    public static string ThreadName(string typeName, string memberName)
    {
        var name = $"{typeName}-{memberName}";
        return name.Length <= MostThreadNameLength ? name : name[..MostThreadNameLength];
    }
}
