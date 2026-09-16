using System.Threading.Channels;
using Modbot.Core.Time;
using Npgsql;
using NpgsqlTypes;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Modbot.Core.Logging.Store;

/// <summary>
/// Writes Modbot's log into its own database, so it can be read in the app.
/// </summary>
/// <remarks>
/// <para>
/// A fourth destination beside the console, the files and Seq, for the operator who has none of the
/// other three: a container whose disk is thrown away on redeploy, no Seq server, and a hosting
/// dashboard that shows the last few hundred lines with no way to search them.
/// </para>
///
/// <para><strong>The rules this sink lives by.</strong></para>
/// <list type="number">
/// <item>
/// <strong>It never blocks the thread that logged.</strong> <see cref="Emit"/> is one bounded-queue
/// write and nothing else. Rendering the message, serialising the properties and talking to
/// PostgreSQL all happen on this sink's own task.
/// </item>
/// <item>
/// <strong>It drops rather than waits.</strong> A full queue means the database is slow or gone; the
/// answer is to lose log lines, not to slow Modbot down. Dropped lines are counted and the count is
/// on the Health page, so "the log has gaps" is a fact somebody can see rather than a mystery.
/// </item>
/// <item>
/// <strong>It never logs its own failures through Serilog.</strong> That is a loop: the failure
/// writes a line, the line comes back through this sink, the write fails again. Failures go to
/// Serilog's <see cref="SelfLog"/> and into <see cref="Status"/>, and nowhere else.
/// </item>
/// <item>
/// <strong>Outbound API traffic is not stored.</strong> <see cref="LogArea.Http"/> events are
/// dropped in <see cref="Emit"/>, before they cost anything. They are tens of thousands a day on a
/// busy sync and they already have their own file and their own place in Seq.
/// </item>
/// </list>
///
/// <para><strong>Why it is started late.</strong></para>
/// <para>
/// Logging is configured before the database is reachable, let alone migrated — that ordering is
/// deliberate, because the first thing an operator needs to read is why the database could not be
/// reached. So the sink is built with the logger, queues everything from the first line, and is
/// handed a connection by <see cref="Start"/> only once the tables exist. Startup lines are kept in
/// the queue until then rather than lost, which is how "Modbot started" ends up in the table.
/// </para>
/// </remarks>
public sealed class DatabaseLogSink : ILogEventSink, IAsyncDisposable
{
    /// <summary>
    /// Lines held while the database is slow or not connected yet. About 10 MB of rendered text at
    /// the ceiling, and enough to hold every line a slow start writes.
    /// </summary>
    public const int QueueCapacity = 10_000;

    /// <summary>Rows in one write. Large enough that a busy minute is a handful of statements.</summary>
    public const int BatchSize = 500;

    /// <summary>How long a partly-filled batch waits for company before it is written anyway.</summary>
    public static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long the writer waits after a failed batch before trying the next one.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly Channel<LogEvent> _queue = Channel.CreateBounded<LogEvent>(
        new BoundedChannelOptions(QueueCapacity)
        {
            // Wait, with TryWrite: the writer never actually waits, it is told "no" and counts a
            // drop. DropOldest would lose the line silently and leave nothing to count.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly IModbotClock _clock;
    private readonly CancellationTokenSource _stopping = new();

    private NpgsqlDataSource? _source;
    private Task? _writer;

    private long _written;
    private long _dropped;
    private DateTimeOffset? _lastWriteAt;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;

    public DatabaseLogSink(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>What the sink has been doing, for the Health page.</summary>
    public LogStoreStatus Status => new(
        _writer is not null,
        Interlocked.Read(ref _written),
        Interlocked.Read(ref _dropped),
        _lastWriteAt,
        _lastError,
        _lastErrorAt,
        _queue.Reader.Count);

    /// <summary>
    /// Connects the sink to the database and starts writing. Called once, after the migrations have
    /// run. Calling it a second time does nothing.
    /// </summary>
    public void Start(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (_writer is not null)
            return;

        // Its own data source, small on purpose: the log writer is one task writing one batch at a
        // time, and it must never be the reason a request cannot get a connection.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 2,
            ApplicationName = "modbot-log-store",
        };

        _source = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        _writer = Task.Run(() => RunAsync(_stopping.Token));
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (_stopping.IsCancellationRequested)
            return;

        // Outbound API traffic has its own file and its own place in Seq, and there is far too much
        // of it to keep for six months.
        if (logEvent.Properties.TryGetValue(LogArea.Name, out var area)
            && area is ScalarValue { Value: LogArea.Http })
        {
            return;
        }

        if (!_queue.Writer.TryWrite(logEvent))
            Interlocked.Increment(ref _dropped);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<LogEvent>(BatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    return;

                // One line woke us; give the rest of the burst a moment to arrive so a busy second
                // is one statement rather than two hundred.
                await Task.Delay(BatchDelay, ct).ConfigureAwait(false);

                batch.Clear();
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var next))
                    batch.Add(next);

                if (batch.Count == 0)
                    continue;

                await WriteAsync(batch, ct).ConfigureAwait(false);

                Interlocked.Add(ref _written, batch.Count);
                _lastWriteAt = _clock.UtcNow;
                _lastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Never through Serilog: that line would come back through this sink and fail again.
                Note(e);

                try
                {
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// One COPY for the batch. A multi-row INSERT would do, but COPY is what PostgreSQL is fastest
    /// at and it keeps the cost of an unusually chatty minute off the database.
    /// </summary>
    private async Task WriteAsync(List<LogEvent> batch, CancellationToken ct)
    {
        var source = _source ?? throw new InvalidOperationException("The log sink has no database.");

        await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY modbot_log (at, level, message, template, source, area, exception, properties)
            FROM STDIN (FORMAT BINARY)
            """,
            ct).ConfigureAwait(false);

        foreach (var logEvent in batch)
        {
            var row = LogRow.From(logEvent);

            await writer.StartRowAsync(ct).ConfigureAwait(false);
            await writer.WriteAsync(row.At, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
            await writer.WriteAsync(row.Level, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await writer.WriteAsync(row.Message, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await WriteNullableAsync(writer, row.Template, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await WriteNullableAsync(writer, row.Source, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(writer, row.Area, NpgsqlDbType.Varchar, ct).ConfigureAwait(false);
            await WriteNullableAsync(writer, row.Exception, NpgsqlDbType.Text, ct).ConfigureAwait(false);
            await writer.WriteAsync(row.Properties, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
        }

        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteNullableAsync(
        NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct)
    {
        if (value is null)
            await writer.WriteNullAsync(ct).ConfigureAwait(false);
        else
            await writer.WriteAsync(value, type, ct).ConfigureAwait(false);
    }

    private void Note(Exception e)
    {
        _lastError = e.Message;
        _lastErrorAt = _clock.UtcNow;
        SelfLog.WriteLine("Modbot log store could not write: {0}", e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
            return;

        await _stopping.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        if (_writer is not null)
        {
            try
            {
                await _writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down is the only way out of the loop.
            }
        }

        if (_source is not null)
            await _source.DisposeAsync().ConfigureAwait(false);

        _stopping.Dispose();
    }
}
