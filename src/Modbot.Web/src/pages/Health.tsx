import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { POSTURE, TONE } from '@/lib/gate'
import { ago, duration, formatDay } from '@/lib/format'
import { api, ApiError, type SyncHealth } from '@/lib/api'
import { cn } from '@/lib/utils'
import { AlertTriangle } from 'lucide-react'

/**
 * What the gate and the producers would tell an operator about themselves (spec 4.2.3, 4.3.3).
 *
 * The screen is ordered by how much somebody has to care, not by subsystem. A mapping defect —
 * an audit-log event type Modbot is waiting for that VRChat does not send — comes first, because
 * it is the one failure here that is otherwise completely silent: facts of that type are being
 * lost right now, the fact log looks healthy, and nothing else in Modbot will ever mention it.
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

  const posture = POSTURE[health.gate.posture]
  const Icon = posture.icon

  return (
    <div className="flex flex-col gap-4">
      {health.vocabulary?.hasProblem && (
        <Defect vocabulary={health.vocabulary} />
      )}

      <Card>
        <CardContent className="py-4">
          <div className="flex items-start gap-3">
            <Icon className={cn('mt-0.5 size-5 shrink-0', TONE[posture.tone])} />
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <span className="font-medium">VRChat · {posture.label}</span>
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
              health.auditLogCadence
                ? `Polling every ${duration(health.auditLogCadence.intervalSeconds)} — ${health.auditLogCadence.reason}`
                : 'No cadence decision published yet.'
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
            {health.auditLogBackfillComplete
              ? 'The one-off walk back through VRChat’s existing audit log has finished.'
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
          <div className="mb-1 font-medium">Audit-log vocabulary</div>
          {health.vocabulary ? (
            <>
              <p className="mb-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                Checked against VRChat’s own declared list{' '}
                {ago(health.vocabulary.checkedAt, health.now)}. VRChat declares{' '}
                {health.vocabulary.declared.length} event types for this group.
              </p>
              {health.vocabulary.unmapped.length > 0 ? (
                <>
                  <div style={{ fontSize: 'var(--text-small)' }}>
                    {health.vocabulary.unmapped.length} declared type
                    {health.vocabulary.unmapped.length === 1 ? '' : 's'} Modbot does not record.
                    These are things happening in the group that are not becoming facts — a known
                    gap rather than a fault.
                  </div>
                  <ul className="mt-1 flex flex-wrap gap-1.5 font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    {health.vocabulary.unmapped.map((t) => (
                      <li key={t} className="rounded border px-1.5" style={{ borderWidth: 'var(--hairline)' }}>
                        {t}
                      </li>
                    ))}
                  </ul>
                </>
              ) : (
                !health.vocabulary.hasProblem && (
                  <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    Every type VRChat declares is mapped, and every mapping Modbot depends on is
                    declared.
                  </p>
                )
              )}
            </>
          ) : (
            <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Not checked yet. The check runs shortly after start-up and about daily thereafter;
              until it has, Modbot’s mapping table is unverified against this group.
            </p>
          )}

          {health.unmappedAuditEvents.length > 0 && (
            <div className="mt-4">
              <div style={{ fontSize: 'var(--text-small)' }}>
                Event types actually seen and not understood, since this process started:
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
                Each of these is a bug report worth filing: either VRChat has an event type this
                project has not catalogued, or the name in Modbot’s mapping table is wrong.
              </p>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

/**
 * The loudest thing on the screen, and the only one that is otherwise silent.
 *
 * A spelling Modbot treats as real that VRChat does not declare means Modbot is waiting for a
 * string that will never arrive. No event is reported unmapped, no error is logged, the fact log
 * looks healthy — and every event of that type is being dropped. It is a defect with an owner and
 * a fix, so it is presented as one rather than as a statistic.
 */
function Defect({ vocabulary }: { vocabulary: NonNullable<SyncHealth['vocabulary']> }) {
  return (
    <div
      className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-3"
      style={{ borderWidth: 'var(--hairline)' }}
    >
      <div className="flex items-center gap-2 font-medium text-destructive">
        <AlertTriangle className="size-4 shrink-0" />
        Facts are being lost right now
      </div>
      <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Modbot is watching for {vocabulary.missingPrimary.length} audit-log event type
        {vocabulary.missingPrimary.length === 1 ? '' : 's'} that VRChat does not declare for this
        group. Whatever those map to is not being recorded, and nothing else will tell you: no
        event is reported as unmapped, because the name Modbot is waiting for is one VRChat never
        sends.
      </p>
      <ul className="mt-2 flex flex-wrap gap-1.5 font-mono" style={{ fontSize: 'var(--text-small)' }}>
        {vocabulary.missingPrimary.map((t) => (
          <li key={t} className="rounded border border-destructive/40 px-1.5" style={{ borderWidth: 'var(--hairline)' }}>
            {t}
          </li>
        ))}
      </ul>
      <p className="mt-2 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        This is a defect in Modbot, not in your deployment. Report it with the list above and the
        types VRChat does declare; the fix is one table of constants.
      </p>
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
