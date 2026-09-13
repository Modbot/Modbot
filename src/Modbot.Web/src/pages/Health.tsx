import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { statusOf, TONE } from '@/lib/gate'
import { ago, duration, formatDay } from '@/lib/format'
import { api, ApiError, type SyncHealth } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * What the gate and the producers would tell an operator about themselves (spec 4.2.3, 4.3.3).
 *
 * The screen is ordered by how much somebody has to care, not by subsystem: whether VRChat is
 * reachable, then whether the producers are running, then the budgets, then the event types
 * Modbot has seen and not understood.
 */

export function Health() {
  const [health, setHealth] = useState<SyncHealth | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    const load = () =>
      api
        .syncHealth()
        .then((next) => {
          if (!cancelled) {
            setHealth(next)
            setError(null)
          }
        })
        .catch((e: unknown) => {
          if (cancelled) return
          setError(
            e instanceof ApiError && e.status === 403
              ? 'Reading Modbot’s operational record is a separate permission, and this account does not hold it.'
              : 'Could not load sync health.',
          )
        })

    void load()

    // Polled rather than pushed. The numbers move on the producers' own schedule, and a screen
    // somebody leaves open should not go stale into a decision.
    const timer = setInterval(() => void load(), 20_000)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  if (!health) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
      </Card>
    )
  }

  const status = statusOf(health.gate.status)
  const Icon = status.icon

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardContent className="py-4">
          <div className="flex items-start gap-3">
            <Icon className={cn('mt-0.5 size-5 shrink-0', TONE[status.tone])} />
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <span className="font-medium">VRChat · {status.label}</span>
                <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  gate state: {health.gate.state}
                </span>
              </div>
              <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                {health.gate.headline}
              </p>
              {health.gate.coldStopEndsAt && (
                <p className="mt-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                  Next probe no earlier than{' '}
                  {new Date(health.gate.coldStopEndsAt).toLocaleTimeString()}. Modbot sends exactly
                  one, because a probe issued during a penalty extends it.
                </p>
              )}
            </div>
          </div>
        </CardContent>
      </Card>

      {!health.groupConfigured && (
        <Note>
          No managed group is configured, so both producers are idle by design. Finish the setup
          wizard and they start on their own.
        </Note>
      )}

      {!health.syncRunningInThisProcess && (
        <Note>
          The sync producers are not registered in this process. Nothing below is running — which
          is different from everything being idle, and is a deployment problem rather than a quiet
          group.
        </Note>
      )}

      <Card>
        <CardContent className="py-4">
          <div className="mb-3 font-medium">Producers</div>

          <Producer
            name="Group audit log"
            polledAt={health.auditLogPolledAt}
            now={health.now}
            detail={
              health.auditLogPollRate
                ? `Polling every ${duration(health.auditLogPollRate.intervalSeconds)} — ${health.auditLogPollRate.reason}`
                : 'No poll rate decision published yet.'
            }
            run={health.lastAuditLogRun}
          />

          <Producer
            name="Group info"
            polledAt={health.groupInfoPolledAt}
            now={health.now}
            detail="Re-reads the group's name, roles and member counts, and writes a fact only when something changed."
            run={health.lastGroupInfoRun}
          />

          <p className="mt-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {health.auditLogCatchUpComplete
              ? health.auditLogHistoryHorizon
                ? `The one-off walk back through VRChat’s existing audit log stopped where VRChat stops paging, after ${health.auditLogHistoryHorizon.entriesRead} entries. Anything older stays in VRChat’s own log.`
                : 'The one-off walk back through VRChat’s existing audit log has finished.'
              : 'Still walking back through the audit log VRChat already held. Until that finishes, the start of Modbot’s history is still moving backwards.'}
            {health.auditLogSyncedThrough &&
              ` Consumed through ${formatDay(health.auditLogSyncedThrough)}.`}
          </p>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="py-4">
          <div className="mb-1 font-medium">Rate-limit budgets</div>
          <p className="mb-3 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            One budget per endpoint class. A limit hit on one stops only that one — which is why
            a cold-stopped member sweep does not stop the audit log. A multiplier below 1.00 means
            a 429 halved that budget and it is recovering by a small step per hour.
          </p>

          {health.buckets.length === 0 ? (
            <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              No bucket has been used yet, so there is nothing to report.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="py-1 text-left font-normal">Bucket</th>
                    <th className="py-1 text-right font-normal">Rate (req/s)</th>
                    <th className="py-1 text-right font-normal">Budget</th>
                    <th className="py-1 text-right font-normal">429s</th>
                    <th className="py-1 text-left font-normal">State</th>
                  </tr>
                </thead>
                <tbody>
                  {health.buckets.map((bucket) => (
                    <tr key={bucket.name} className="border-b last:border-0" style={{ borderBottomWidth: 'var(--hairline)' }}>
                      <td className="py-1 font-mono">{bucket.name}</td>
                      <td className="py-1 text-right tabular-nums">
                        {bucket.effectiveRatePerSecond.toFixed(3)}
                      </td>
                      <td
                        className={cn(
                          'py-1 text-right tabular-nums',
                          bucket.budgetMultiplier < 1 && 'text-warn',
                        )}
                      >
                        {bucket.budgetMultiplier.toFixed(2)}×
                      </td>
                      <td className="py-1 text-right tabular-nums">{bucket.rateLimitHits}</td>
                      <td className="py-1">
                        {bucket.alerting ? (
                          <span className="text-destructive">
                            given up — needs you
                          </span>
                        ) : bucket.isColdStopped ? (
                          <span className="text-warn">
                            cold-stopped
                            {bucket.stoppedUntil
                              ? ` until ${new Date(bucket.stoppedUntil).toLocaleTimeString()}`
                              : ''}
                          </span>
                        ) : (
                          <span className="text-muted-foreground">running</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardContent className="py-4">
          <div className="mb-1 font-medium">Audit-log event types Modbot does not understand</div>
          {health.unmappedAuditEvents.length === 0 ? (
            <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              None seen since this process started. Every audit-log entry VRChat has sent had an
              event type Modbot has a name for.
            </p>
          ) : (
            <div>
              <div style={{ fontSize: 'var(--text-small)' }}>
                Seen since this process started. Each is still recorded — as an unrecognised event
                with VRChat’s own wording kept — so nothing is lost; each is also a mapping worth
                adding.
              </div>
              <table className="mt-1 w-full" style={{ fontSize: 'var(--text-small)' }}>
                <tbody>
                  {health.unmappedAuditEvents.map((e) => (
                    <tr key={e.eventType}>
                      <td className="py-1 pr-3 font-mono">{e.eventType}</td>
                      <td className="py-1 pr-3 tabular-nums text-muted-foreground">{e.count}×</td>
                      <td className="py-1 text-muted-foreground">
                        {e.sampleDescription ?? e.sampleEntryId ?? ''}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <p className="mt-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Each of these is a bug report worth filing: VRChat has an event type this project
                has not catalogued yet.
              </p>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function Producer({
  name,
  polledAt,
  now,
  detail,
  run,
}: {
  name: string
  polledAt: string | null
  now: string
  detail: string
  run: SyncHealth['lastAuditLogRun']
}) {
  return (
    <div className="border-b py-2 last:border-0" style={{ borderBottomWidth: 'var(--hairline)' }}>
      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="font-medium">{name}</span>
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          last completed a pass {ago(polledAt, now)}
        </span>
      </div>
      <p className="mt-0.5 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        {detail}
      </p>
      {run && (
        <p className="mt-0.5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Last run in this process: {run.outcome.toLowerCase()} — {run.summary} (
          {run.durationSeconds.toFixed(1)}s, {ago(run.at, now)})
        </p>
      )}
    </div>
  )
}

function Note({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3 text-muted-foreground"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      {children}
    </div>
  )
}
