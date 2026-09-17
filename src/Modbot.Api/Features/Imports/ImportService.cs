using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Imports;

/// <summary>
/// Tells the import loop that an upload has been queued, so it does not wait out its poll.
/// </summary>
/// <remarks>Same shape as <c>FactSignal</c>: a pulse is a reason to look, never the work itself.</remarks>
public sealed class ImportSignal
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Next() => Volatile.Read(ref _next).Task;

    public void Pulse()
    {
        var fired = Interlocked.Exchange(
            ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        fired.TrySetResult();
    }
}

/// <summary>
/// Runs queued imports, one at a time, for the life of the process (import design §8).
/// </summary>
/// <remarks>
/// <para>
/// One at a time on purpose: two imports of the same file at once would race the duplicate
/// check, and a job that writes thousands of facts is not one to run beside itself.
/// </para>
/// <para>
/// An import found <c>Running</c> at startup was cut off by a restart. It is marked failed with
/// that as the reason: the batches it committed are in the log and in the dedupe table, so
/// uploading the file again imports only what was left.
/// </para>
/// </remarks>
public sealed class ImportService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ImportSignal _signal;
    private readonly ILogger<ImportService> _log;

    public ImportService(IServiceScopeFactory scopes, ImportSignal signal, ILogger<ImportService> log)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(log);

        _scopes = scopes;
        _signal = signal;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailInterruptedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = _signal.Next();
            var delay = PollInterval;

            try
            {
                using var scope = _scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<ImportRunner>();

                if (await runner.RunNextAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The runner marks its own import failed; this is the loop itself failing, for
                // example the database being away. Wait longer, and keep going.
                _log.LogWarning(e, "The import loop failed; trying again in {Delay}", RetryDelay);
                delay = RetryDelay;
            }

            try
            {
                await Task.WhenAny(next, Task.Delay(delay, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task FailInterruptedAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var now = scope.ServiceProvider.GetRequiredService<IModbotClock>().UtcNow;

            var failed = await db.Imports
                .Where(i => i.Status == ImportStatus.Running)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(i => i.Status, ImportStatus.Failed)
                        .SetProperty(i => i.Error, "Modbot restarted before this import finished. Upload the file again; records already imported are skipped.")
                        .SetProperty(i => i.FinishedAt, now)
                        .SetProperty(i => i.Body, (byte[]?)null),
                    ct);

            if (failed > 0)
                _log.LogInformation("Marked {Count} import(s) failed: interrupted by a restart", failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not check for imports interrupted by a restart");
        }
    }
}
