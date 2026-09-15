using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Messages;

/// <summary>What one pass of reading history did, for the log and the tests.</summary>
/// <param name="Pages">Requests made for pages of messages.</param>
/// <param name="Stored">Messages stored that were not stored before.</param>
/// <param name="ChannelsFinished">Channels and threads whose read-back finished during this pass.</param>
public sealed record DiscordHistoryPass(int Pages, long Stored, int ChannelsFinished);

/// <summary>
/// Reads the server's message history into the store: back through every channel and thread the
/// bot can read, on first setup and whenever a channel has more to read (M5 spec §5.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The stop rule.</strong> A channel is read newest first, a hundred at a time. It stops at
/// its first message, or once three pages in a row hold only messages already stored: history
/// beyond them has been read before, and reading it again is wasted requests. A page with anything
/// new on it starts the count again.
/// </para>
/// <para>
/// <strong>Carrying on after a restart.</strong> Each page's oldest message is saved with the count
/// before the next page is asked for, so a restart picks the channel up at the page it had reached.
/// </para>
/// <para>
/// <strong>Pace.</strong> One page at a time for the whole server, with
/// <see cref="DiscordBotOptions.ReadPause"/> between pages. The library holds each request to
/// Discord's rate limits and waits out a 429 on its own; nothing here retries a refused request --
/// a page that fails is left for the next pass.
/// </para>
/// </remarks>
public sealed class DiscordHistoryReader
{
    /// <summary>Pages in a row of only stored messages that stop a channel.</summary>
    public const int StoredPagesToStop = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly DiscordBotOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public DiscordHistoryReader(
        IServiceScopeFactory scopes,
        IModbotClock clock,
        DiscordBotOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _scopes = scopes;
        _clock = clock;
        _options = options;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>
    /// The kinds of channel whose own history holds messages. Forums and media channels hold only
    /// threads; categories hold nothing.
    /// </summary>
    public static readonly IReadOnlySet<string> MessageChannelTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        DiscordChannelTypes.Text,
        DiscordChannelTypes.Announcement,
        DiscordChannelTypes.Voice,
        DiscordChannelTypes.Stage,
    };

    /// <summary>The kinds of channel threads live in.</summary>
    public static readonly IReadOnlySet<string> ThreadChannelTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        DiscordChannelTypes.Text,
        DiscordChannelTypes.Announcement,
        DiscordChannelTypes.Forum,
        DiscordChannelTypes.Media,
    };

    /// <summary>
    /// Reads back every channel and thread that has more to read, until each is finished or the
    /// pass is cancelled.
    /// </summary>
    public async Task<DiscordHistoryPass> ReadBackAsync(IDiscordGateway gateway, string guildId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);

        await PrepareAsync(gateway, guildId, ct).ConfigureAwait(false);

        var pages = 0;
        var stored = 0L;
        var finished = 0;

        foreach (var channelId in await UnfinishedAsync(guildId, ct).ConfigureAwait(false))
        {
            while (!ct.IsCancellationRequested)
            {
                var step = await ReadBackPageAsync(gateway, channelId, ct).ConfigureAwait(false);
                if (step is null)
                    break;

                pages++;
                stored += step.Value.Stored;

                if (step.Value.Finished)
                    finished++;

                if (step.Value.Finished || step.Value.Failed)
                    break;

                await _delay(_options.ReadPause, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
        }

        if (pages > 0)
        {
            _log.Information(
                "Read back {Pages} pages of Discord history: {Stored} messages stored, {Finished} channels finished",
                pages,
                stored,
                finished);
        }

        return new DiscordHistoryPass(pages, stored, finished);
    }

    /// <summary>
    /// Reads every channel and open thread forward from its newest stored message until nothing
    /// newer is left: the messages posted while the bot was away (M5 spec §5.1).
    /// </summary>
    /// <remarks>
    /// The three-page stop rule does not apply going forward -- every page past the newest stored
    /// message is new by definition, and stopping early would leave a hole. A channel with nothing
    /// stored yet is skipped: its read-back starts from the newest message anyway. Archived threads
    /// are skipped too; posting in one opens it again, and then it is in the session's list.
    /// </remarks>
    public async Task<DiscordHistoryPass> CatchUpAsync(IDiscordGateway gateway, string guildId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(guildId);

        List<(string Id, bool Thread)> targets;

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var channels = await db.DiscordChannels.AsNoTracking()
                .Where(c => c.GuildId == guildId && c.RemovedAt == null && c.BotCanView && c.BotCanReadHistory)
                .OrderBy(c => c.Position)
                .Select(c => new { c.ChannelId, c.Type })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            targets = channels
                .Where(c => MessageChannelTypes.Contains(c.Type))
                .Select(c => (c.ChannelId, false))
                .ToList();

            var containers = channels.Where(c => ThreadChannelTypes.Contains(c.Type)).Select(c => c.ChannelId).ToList();
            if (containers.Count > 0)
            {
                var open = await gateway.ReadThreadsAsync(guildId, containers, [], ct).ConfigureAwait(false);
                targets.AddRange(open.Where(t => !t.Archived).Select(t => (t.Id, true)));
            }
        }

        var pages = 0;
        var stored = 0L;

        foreach (var (id, thread) in targets)
        {
            var newest = await NewestStoredAsync(id, thread, ct).ConfigureAwait(false);
            if (newest is null)
                continue;

            while (!ct.IsCancellationRequested)
            {
                var page = await gateway.ReadMessagesAsync(id, beforeId: null, afterId: newest, ct).ConfigureAwait(false);
                pages++;

                if (page.Error is not null)
                {
                    _log.Debug("Could not catch up channel {ChannelId}: {Reason}", id, page.Error);
                    break;
                }

                using (var scope = _scopes.CreateScope())
                {
                    var handler = scope.ServiceProvider.GetRequiredService<DiscordMessageHandler>();
                    stored += await handler.ReceivedAsync(page.Messages, ct).ConfigureAwait(false);
                }

                newest = page.NewestId ?? newest;

                await _delay(_options.ReadPause, ct).ConfigureAwait(false);

                if (!page.Full)
                    break;
            }

            ct.ThrowIfCancellationRequested();
        }

        if (stored > 0)
            _log.Information("Caught up {Stored} Discord messages posted while the bot was away, in {Pages} pages", stored, pages);

        return new DiscordHistoryPass(pages, stored, 0);
    }

    /// <summary>The newest message stored in a channel -- not counting its threads -- or in a thread.</summary>
    private async Task<string?> NewestStoredAsync(string id, bool thread, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var messages = thread
            ? db.DiscordMessages.AsNoTracking().Where(m => m.ThreadId == id)
            : db.DiscordMessages.AsNoTracking().Where(m => m.ChannelId == id && m.ThreadId == null);

        return await messages
            .OrderByDescending(m => m.SentAt)
            .Select(m => m.MessageId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure every readable channel and thread has a read-back row, and reopens a channel the
    /// bot has since been given access to.
    /// </summary>
    private async Task PrepareAsync(IDiscordGateway gateway, string guildId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var now = _clock.UtcNow;

        var channels = await db.DiscordChannels
            .Where(c => c.GuildId == guildId && c.RemovedAt == null && c.BotCanView && c.BotCanReadHistory)
            .OrderBy(c => c.Position)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = await db.DiscordReadBacks
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.ChannelId, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        // Archived threads cost requests to list, so they are listed only for a channel that is
        // new here or still being read back. A thread archived later was open -- and so in the
        // session's own list -- while its channel was watched.
        var listArchived = channels
            .Where(c => ThreadChannelTypes.Contains(c.Type))
            .Where(c => !rows.TryGetValue(c.ChannelId, out var r) || r.FinishedAt is null || r.StoppedBecause == DiscordReadBackStops.NoAccess)
            .Select(c => c.ChannelId)
            .ToList();

        foreach (var channel in channels.Where(c => MessageChannelTypes.Contains(c.Type) || ThreadChannelTypes.Contains(c.Type)))
        {
            if (!rows.TryGetValue(channel.ChannelId, out var row))
            {
                row = New(channel.ChannelId, guildId, null, channel.Name, now);
                db.DiscordReadBacks.Add(row);
                rows[row.ChannelId] = row;
            }
            else if (row.StoppedBecause == DiscordReadBackStops.NoAccess)
            {
                Reopen(row, now);
            }

            // A forum holds no messages of its own, only threads: its row says the threads were
            // listed, and is done as soon as they are.
            if (!MessageChannelTypes.Contains(channel.Type) && row.FinishedAt is null)
                Finish(row, DiscordReadBackStops.Start, now);
        }

        var containers = channels
            .Where(c => ThreadChannelTypes.Contains(c.Type))
            .Select(c => c.ChannelId)
            .ToHashSet(StringComparer.Ordinal);

        if (containers.Count > 0)
        {
            var threads = await gateway
                .ReadThreadsAsync(guildId, containers.ToList(), listArchived, ct)
                .ConfigureAwait(false);

            foreach (var thread in threads.Where(t => containers.Contains(t.ParentChannelId)))
            {
                if (!rows.TryGetValue(thread.Id, out var row))
                {
                    row = New(thread.Id, guildId, thread.ParentChannelId, thread.Name, now);
                    db.DiscordReadBacks.Add(row);
                    rows[row.ChannelId] = row;
                }
                else if (row.StoppedBecause == DiscordReadBackStops.NoAccess)
                {
                    Reopen(row, now);
                }
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> UnfinishedAsync(string guildId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // Channels before threads, and the ones that started first first, so a restart carries on
        // with the channel it was part way through.
        return await db.DiscordReadBacks.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.FinishedAt == null)
            .OrderBy(r => r.ParentChannelId != null)
            .ThenBy(r => r.PagesRead == 0)
            .ThenBy(r => r.StartedAt)
            .ThenBy(r => r.ChannelId)
            .Select(r => r.ChannelId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one page further back through one channel and saves where it got to. Null when the
    /// channel has nothing left to read.
    /// </summary>
    public async Task<(long Stored, bool Finished, bool Failed)?> ReadBackPageAsync(
        IDiscordGateway gateway, string channelId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var row = await db.DiscordReadBacks.FirstOrDefaultAsync(r => r.ChannelId == channelId, ct).ConfigureAwait(false);
        if (row is null || row.FinishedAt is not null)
            return null;

        var page = await gateway.ReadMessagesAsync(channelId, row.OldestReadId, afterId: null, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        row.UpdatedAt = now;

        if (page.Error is not null)
        {
            row.LastError = page.Error;

            if (page.NoAccess)
                Finish(row, DiscordReadBackStops.NoAccess, now);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (0, page.NoAccess, !page.NoAccess);
        }

        // Stored without an AI moderation check: history is not moderated.
        var store = scope.ServiceProvider.GetRequiredService<DiscordMessageStore>();
        var fresh = await store.StoreAsync(page.Messages, ct).ConfigureAwait(false);

        row.PagesRead++;
        row.MessagesStored += fresh.Count;
        row.LastError = null;
        row.OldestReadId = page.OldestId ?? row.OldestReadId;

        if (fresh.Count > 0)
            row.StoredPagesInARow = 0;
        else if (page.Messages.Count > 0)
            row.StoredPagesInARow++;

        if (!page.Full)
            Finish(row, DiscordReadBackStops.Start, now);
        else if (row.StoredPagesInARow >= StoredPagesToStop)
            Finish(row, DiscordReadBackStops.AlreadyStored, now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (fresh.Count, row.FinishedAt is not null, false);
    }

    private static DiscordReadBack New(string channelId, string guildId, string? parent, string name, DateTimeOffset now) => new()
    {
        ChannelId = channelId,
        GuildId = guildId,
        ParentChannelId = parent,
        Name = name,
        StartedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Starts a channel the bot could not read over from the newest message. The stop rule keeps
    /// that cheap when some of it was read before.
    /// </summary>
    private static void Reopen(DiscordReadBack row, DateTimeOffset now)
    {
        row.FinishedAt = null;
        row.StoppedBecause = null;
        row.OldestReadId = null;
        row.StoredPagesInARow = 0;
        row.UpdatedAt = now;
    }

    private static void Finish(DiscordReadBack row, string because, DateTimeOffset now)
    {
        row.FinishedAt = now;
        row.StoppedBecause = because;
    }
}
