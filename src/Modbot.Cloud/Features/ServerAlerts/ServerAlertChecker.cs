using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.ServerLogs;
using Modbot.Cloud.Features.Mail;

namespace Modbot.Cloud.Features.ServerAlerts;

/// <param name="Problems">Deployments that went wrong and were emailed about.</param>
/// <param name="Recoveries">Deployments that came back and were emailed about.</param>
/// <param name="Sent">Emails that went.</param>
public sealed record ServerAlertRun(
    IReadOnlyList<Guid> Problems,
    IReadOnlyList<Guid> Recoveries,
    int Sent);

/// <summary>
/// Checks each watched deployment from outside and emails what changed.
/// </summary>
/// <remarks>
/// <para>
/// Two checks, because two are all Cloud can honestly make:
/// </para>
/// <list type="number">
/// <item>
/// <strong>Silence.</strong> Nothing has arrived from this deployment for longer than its window.
/// That covers the cases Modbot cannot report about itself — the process is not running, the
/// database is gone, the host is unreachable — and it covers them without Cloud needing to know the
/// deployment's address or being able to reach it.
/// </item>
/// <item>
/// <strong>Errors.</strong> More errors in the last hour than the operator asked to hear about. Off
/// unless a number is set, because "some errors" is normal and the right number is theirs.
/// </item>
/// </list>
/// <para>
/// The state is the row, exactly as in Modbot's own half: a problem emails once and then stays quiet
/// for the quiet time, and a recovery says it is over.
/// </para>
/// <para>
/// <strong>Silence is not proof of a problem</strong> — an operator who turned sending off, or set
/// <c>MODBOT_CLOUD_DISABLED</c>, goes silent too. So the check is only on for a deployment somebody
/// deliberately turned it on for, and the email says what Cloud saw rather than what it concluded.
/// </para>
/// </remarks>
public sealed class ServerAlertChecker(
    CloudContext cloud,
    EngineContext engine,
    ICloudMailer mailer,
    TimeProvider time)
{
    /// <summary>How often the checks run.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    public async Task<ServerAlertRun> RunOnceAsync(CancellationToken ct = default)
    {
        var alerts = await cloud.ServerAlerts.Where(a => a.On).ToListAsync(ct);

        if (alerts.Count == 0)
            return new ServerAlertRun([], [], 0);

        var addresses = await AddressesAsync(alerts, ct);
        var now = time.GetUtcNow();
        var problems = new List<Guid>();
        var recoveries = new List<Guid>();
        var sending = new List<(ServerAlert Alert, string To, string Subject, string Body)>();

        foreach (var alert in alerts)
        {
            if (addresses.GetValueOrDefault(alert.ServerId) is not { Length: > 0 } to)
                continue;

            var (problem, detail) = await LookAsync(alert, now, ct);
            var quiet = TimeSpan.FromHours(Math.Clamp(alert.QuietHours, 0, 168));

            if (problem)
            {
                if (!alert.Problem)
                {
                    alert.Problem = true;
                    alert.Since = now;
                }

                alert.Detail = detail;

                if (alert.LastSentAt is { } last && now - last < quiet)
                    continue;

                alert.LastSentAt = now;
                problems.Add(alert.ServerId);

                sending.Add((
                    alert,
                    to,
                    "Modbot Cloud: your Modbot needs looking at",
                    $"{detail}\n\nServer: {alert.ServerId}\nSince: {alert.Since ?? now:u}\n\n"
                    + "Modbot Cloud noticed this from outside, so it may not be able to tell you itself."));
            }
            else if (alert.Problem)
            {
                var said = alert.LastSentAt is not null;

                alert.Problem = false;
                alert.Since = null;
                alert.Detail = null;
                alert.LastSentAt = null;

                if (!said)
                    continue;

                recoveries.Add(alert.ServerId);

                sending.Add((
                    alert,
                    to,
                    "Modbot Cloud: your Modbot is working again",
                    $"Modbot Cloud is hearing from your deployment again.\n\nServer: {alert.ServerId}"));
            }
        }

        var sent = 0;

        foreach (var (alert, to, subject, body) in sending)
        {
            // The mailer logs why it failed -- with the status and the address's domain, never the
            // key or the body -- so there is nothing to carry back here but whether it went.
            var went = await mailer.SendAsync(to, subject, body, ct).ConfigureAwait(false);

            if (went)
            {
                alert.LastError = null;
                sent++;
            }
            else
            {
                alert.LastError = mailer.CanSend
                    ? "Modbot Cloud could not send the email."
                    : "Modbot Cloud has no mail key set.";

                // The mail did not go, so the quiet time must not start. Otherwise an outage at the
                // mail provider would silently eat the one email that mattered.
                alert.LastSentAt = null;
            }
        }

        await cloud.SaveChangesAsync(ct);

        return new ServerAlertRun(problems, recoveries, sent);
    }

    /// <summary>
    /// Where each watched deployment's mail goes: the address on its row, or the address on the
    /// account that claimed it.
    /// </summary>
    /// <remarks>
    /// An owner who turned this on for their own server should never have had to type their own
    /// address, and an address that follows the account cannot go stale when they change it.
    /// </remarks>
    private async Task<Dictionary<Guid, string?>> AddressesAsync(
        List<ServerAlert> alerts, CancellationToken ct)
    {
        var ids = alerts.Select(a => a.ServerId).ToList();

        var owners = await cloud.RegisteredServers.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && s.AccountId != null)
            .Join(cloud.Accounts.AsNoTracking(), s => s.AccountId, a => a.Id, (s, a) => new { s.Id, a.Email })
            .ToDictionaryAsync(x => x.Id, x => x.Email, ct);

        return alerts.ToDictionary(
            a => a.ServerId,
            a => string.IsNullOrWhiteSpace(a.Email) ? owners.GetValueOrDefault(a.ServerId) : a.Email);
    }

    private async Task<(bool Problem, string Detail)> LookAsync(
        ServerAlert alert, DateTimeOffset now, CancellationToken ct)
    {
        var lastHeard = await engine.ServerLogs.AsNoTracking()
            .Where(l => l.ServerId == alert.ServerId)
            .MaxAsync(l => (DateTimeOffset?)l.ReceivedAt, ct);

        var silentAfter = TimeSpan.FromMinutes(Math.Max(5, alert.SilentAfterMinutes));

        if (lastHeard is null)
            return (false, "");

        if (now - lastHeard.Value > silentAfter)
        {
            return (true,
                $"Modbot Cloud has heard nothing from this Modbot since {lastHeard.Value:u}. "
                + "It may be down, or it may have stopped sending its logs.");
        }

        if (alert.ErrorsAnHour > 0)
        {
            var since = now.AddHours(-1);

            var errors = await engine.ServerLogs.AsNoTracking()
                .Where(l => l.ServerId == alert.ServerId
                            && l.ReceivedAt >= since
                            && (l.Level == LogLevelNames.Error || l.Level == LogLevelNames.Fatal))
                .CountAsync(ct);

            if (errors >= alert.ErrorsAnHour)
                return (true, $"This Modbot has written {errors} error(s) in the last hour.");
        }

        return (false, "");
    }
}
