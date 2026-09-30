using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Calls;
using Modbot.AI.Usage;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using OpenAI.Chat;
using Serilog;

namespace Modbot.AI.Alerts;

/// <summary>
/// Runs every watcher that is on, decides what is unusual, and stores the alerts
/// (AI insights design §8).
/// </summary>
/// <remarks>
/// <para>
/// Never acts on anything it finds (M8 §2). The only things that leave this class are an alert row,
/// a fact, and a Discord post the poster picks up from the row -- the same path an insight takes.
/// </para>
/// <para>
/// The deciding is all <see cref="UnusualRule"/>'s; what is here is reading the settings, holding
/// repeats down, and writing what was found.
/// </para>
/// </remarks>
public sealed class AlertChecker(
    ModbotContext db,
    AlertFigureReader reader,
    IAiClients ai,
    IAiUsage usage,
    IFactWriter facts,
    EventPartitionMaintainer partitions,
    IModbotClock clock,
    AiCallRunner runner)
{
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Analytics);

    /// <summary>How often the watchers run.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(15);

    /// <summary>Runs every watcher that is due. Returns how many alerts were stored.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        var settings = await db.AlertSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (settings is not null && settings.LastCheckedAt is { } last && now - last < CheckEvery)
            return 0;

        // Claimed before anything is read, so two runs that overlap -- or two copies of Modbot
        // pointed at one database -- do not both check the same quarter-hour.
        if (settings is null)
        {
            db.AlertSettings.Add(new AlertSettings { Id = 1, LastCheckedAt = now });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var claimed = await db.AlertSettings
                .Where(s => s.Id == 1 && (s.LastCheckedAt == null || s.LastCheckedAt == settings.LastCheckedAt))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastCheckedAt, now), ct);

            if (claimed == 0)
                return 0;
        }

        var watches = await db.AlertWatches.ToListAsync(ct);
        var on = watches
            .Where(w => AlertWatchers.IsKnown(w.Watcher) && w.Sensitivity != AlertSensitivities.Off)
            .ToList();

        if (on.Count == 0)
            return 0;

        var windows = AlertWindows.At(now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var switchedOn = on.Select(w => w.Watcher).ToHashSet(StringComparer.Ordinal);
        var weekly = on.FirstOrDefault(w => w.Watcher == AlertWatchers.ActiveDrop);

        // The weekly figure is whole UTC days and cannot change inside one, so it is read once a
        // day rather than ninety-six times.
        var wantWeeks = weekly is not null
            && (weekly.CheckedThrough is not { } when || DateOnly.FromDateTime(when.UtcDateTime) < today);

        var readings = await reader.ReadAsync(windows, switchedOn, wantWeeks, ct);

        var written = 0;

        foreach (var watch in on)
        {
            if (watch.Watcher == AlertWatchers.ActiveDrop && !wantWeeks)
                continue;

            // Each window is judged once, however often the checks run.
            if (watch.CheckedThrough is { } through && through >= windows.End)
                continue;

            watch.CheckedThrough = windows.End;

            var found = Judge(watch, windows, readings);
            if (found is null)
                continue;

            if (await HeldDownAsync(watch.Watcher, found.Score, now, settings?.QuietHours ?? AlertWatchers.DefaultQuietHours, ct))
                continue;

            await StoreAsync(found, settings, now, ct);
            written++;
        }

        await db.SaveChangesAsync(ct);

        return written;
    }

    /// <summary>Whether this watcher already said the same thing recently and it has not got much worse.</summary>
    private async Task<bool> HeldDownAsync(string watcher, decimal score, DateTimeOffset now, int quietHours, CancellationToken ct)
    {
        var since = now - TimeSpan.FromHours(Math.Clamp(quietHours, 0, AlertWatchers.MaxQuietHours));

        var recent = await db.Alerts.AsNoTracking()
            .Where(a => a.Watcher == watcher && a.At >= since)
            .OrderByDescending(a => a.At)
            .Select(a => (decimal?)a.Score)
            .FirstOrDefaultAsync(ct);

        return recent is { } posted && !UnusualRule.MuchWorseThan(score, posted);
    }

    /// <summary>Works one watcher's figure out and puts it to the rule.</summary>
    private static AlertFigures? Judge(AlertWatch watch, AlertWindows windows, AlertReadings readings)
    {
        var rule = AlertWatcherRules.For(watch.Watcher);

        if (watch.Watcher == AlertWatchers.InstanceUnwatched)
            return Unwatched(watch, windows, readings, rule);

        var (nowValue, earlier, where) = Figures(watch.Watcher, rule, readings);

        if (earlier is null)
            return null;

        var verdict = UnusualRule.Check(
            new UnusualCheck(nowValue, earlier, watch.Sensitivity, rule.Minimum, rule.Direction, rule.LeastEarlier));

        return verdict.Unusual ? Made(watch, windows, verdict, earlier, where) : null;
    }

    /// <summary>
    /// The busiest open instance nobody is in. Not a change but a state: an instance exactly as full as
    /// every other Friday still wants somebody watching it.
    /// </summary>
    private static AlertFigures? Unwatched(AlertWatch watch, AlertWindows windows, AlertReadings readings, AlertWatcherRule rule)
    {
        var instance = readings.Instances.Where(r => !r.Watched).OrderByDescending(r => r.People).FirstOrDefault();
        if (instance is null)
            return null;

        var normal = UnusualRule.Middle(readings.InstancePeaks);

        if (instance.People < UnusualRule.BusyEnough(normal, watch.Sensitivity, rule.Minimum))
            return null;

        var spread = Math.Max(
            UnusualRule.Middle([.. readings.InstancePeaks.Select(v => Math.Abs(v - normal))]), UnusualRule.LeastSpread);

        var verdict = new UnusualVerdict(true, instance.People, normal, spread, Math.Max(0m, (instance.People - normal) / spread));

        return Made(watch, windows, verdict, readings.InstancePeaks, instance.Where);
    }

    private static (decimal Now, IReadOnlyList<decimal>? Earlier, string? Where) Figures(
        string watcher, AlertWatcherRule rule, AlertReadings readings)
    {
        switch (watcher)
        {
            case AlertWatchers.NewAccounts:
                return (readings.NewAccounts.Count > 0 ? readings.NewAccounts[0] : 0m, Tail(readings.NewAccounts), null);

            case AlertWatchers.InstanceFilling:
            {
                var instance = readings.Instances.OrderByDescending(r => r.People).FirstOrDefault();
                return instance is null ? (0m, null, null) : (instance.People, readings.InstancePeaks, instance.Where);
            }

            case AlertWatchers.ActiveDrop:
                return readings.ActiveWeeks.Count == 0
                    ? (0m, null, null)
                    : (readings.ActiveWeeks[0], Tail(readings.ActiveWeeks), null);

            default:
            {
                var windows = readings.Windows(rule.Types);
                return (windows[0], Tail(windows), null);
            }
        }
    }

    /// <summary>Everything but the window being judged: the matching earlier windows.</summary>
    private static IReadOnlyList<decimal> Tail(IReadOnlyList<decimal> all) => [.. all.Skip(1)];

    private static AlertFigures Made(
        AlertWatch watch, AlertWindows windows, UnusualVerdict verdict, IReadOnlyList<decimal> earlier, string? where)
        => new(
            watch.Watcher,
            AlertWatchers.Label(watch.Watcher),
            AlertWatchers.Counts(watch.Watcher),
            windows.Start,
            windows.End,
            verdict.Now,
            verdict.Normal,
            verdict.Spread,
            decimal.Round(verdict.Score, 2),
            watch.Sensitivity,
            earlier,
            where,
            AlertWatcherRules.LinkFor(watch.Watcher, windows));

    /// <summary>Stores the alert, asks the model for its one sentence, and records the fact.</summary>
    private async Task StoreAsync(AlertFigures figures, AlertSettings? settings, DateTimeOffset now, CancellationToken ct)
    {
        var alert = new Alert
        {
            Watcher = figures.Watcher,
            At = now,
            WindowStart = figures.WindowStart,
            WindowEnd = figures.WindowEnd,
            Now = figures.Now,
            Normal = figures.Normal,
            Spread = figures.Spread,
            Score = figures.Score,
            Sensitivity = figures.Sensitivity,
            Figures = figures.ToJson(),
            Link = figures.Link,
            DiscordChannelId = settings?.DiscordChannelId,
        };

        if (settings?.WriteSentence != false)
            await WriteSentenceAsync(alert, figures, ct);

        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);

        await partitions.EnsureForAsync(now, ct);
        await facts.WriteAsync(new FactRecord
        {
            Type = FactType.InsightAlert,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = figures.Watcher,
            Source = FactSource.Modbot,
            Data = new JsonObject
            {
                ["watcher"] = figures.Watcher,
                ["label"] = figures.Label,
                ["counts"] = figures.Counts,
                ["from"] = figures.WindowStart,
                ["to"] = figures.WindowEnd,
                ["now"] = figures.Now,
                ["normal"] = figures.Normal,
                ["score"] = figures.Score,
                ["sensitivity"] = figures.Sensitivity,
                ["where"] = figures.Where,
                ["link"] = figures.Link,
                ["text"] = alert.Text,
            },
        }, ct);
    }

    /// <summary>
    /// One AI sentence, or none. An alert with no sentence is a complete alert: the figures are the
    /// alert, and the sentence is a courtesy that AI being off or out of budget must not withhold.
    /// </summary>
    /// <remarks>
    /// Made through <see cref="AiCallRunner"/>, like an insight: the feature's timeout, the fallback
    /// model, and a row in the call log whatever comes of it, so a moderator looking at the call log
    /// can see why an alert has no sentence. It counts under Insights and asks for no person: an
    /// alert is nobody's request, so it counts against no team member's allowance.
    /// </remarks>
    private async Task WriteSentenceAsync(Alert alert, AlertFigures figures, CancellationToken ct)
    {
        try
        {
            var chat = await ai.GetChatAsync(ct);
            if (chat is null)
                return;

            // The same budget as insights, read through the shared place every AI feature asks.
            if (await usage.LimitReachedAsync(AiFeatures.Insights, ct) is { } reached)
            {
                await runner.RecordLimitedAsync(AiFeatures.Insights, chat.Model, chat.Provider, reached.Message, null, ct);
                return;
            }

            var stored = await db.InsightSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
            var model = string.IsNullOrWhiteSpace(stored?.Model) ? chat.Model : stored.Model.Trim();

            var instructions = AlertPrompt.Instructions();
            var question = AlertPrompt.Figures(figures);

            // KeepText stays off: nobody pressed a button, so the call log keeps counts, not the text.
            var plan = new AiCallPlan(
                AiFeatures.Insights, chat, model,
                Prompt: $"{instructions}\n\n{question}");

            var result = await runner.RunAsync(plan, async (client, token) =>
            {
                var options = new ChatCompletionOptions { MaxOutputTokenCount = AlertPrompt.MaxOutputTokens };
                AiReportedCost.AskFor(options, chat.Provider);

                ChatCompletion completion = await client.CompleteChatAsync(
                    [
                        new SystemChatMessage(instructions),
                        new UserChatMessage(question),
                    ],
                    options,
                    token);

                var answer = string.Concat(completion.Content
                    .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                    .Select(p => p.Text)).Trim();

                return new AiCallAnswer<string>(answer, completion.Model, completion.Usage, answer);
            }, ct);

            // A call that did not answer is in the call log with its error; the alert goes out without.
            var text = result.Answered ? result.Value ?? string.Empty : string.Empty;
            if (text.Length == 0)
                return;

            alert.Text = text.Length <= AlertPrompt.MaxTextLength
                ? text
                : string.Concat(text.AsSpan(0, AlertPrompt.MaxTextLength), "…");

            // The model that answered, which may be the fallback rather than the one asked for.
            alert.Model = result.Model.Length <= AiSettingsRules.MaxModelLength ? result.Model : model;
            alert.Provider = chat.Provider;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Not put on the alert: it is about the figures, and a provider having a bad afternoon is
            // not something to show a moderator on this card. What the runner could record is in the
            // call log; this catches only what it could not.
            _log.Warning(e, "Could not write the sentence for an alert on {Watcher}", figures.Watcher);
        }
    }
}
