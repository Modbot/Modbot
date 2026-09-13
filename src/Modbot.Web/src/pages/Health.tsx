import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { statusOf, TONE } from '@/lib/gate'
import { ago, duration, formatDay } from '@/lib/format'
import { api, ApiError, type DiscordBotHealth, type SyncHealth } from '@/lib/api'
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

      {health.discordBot && <DiscordBot bot={health.discordBot} now={health.now} />}

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

          <Producer
            name="User profiles"
            polledAt={health.userProfilePolledAt}
            now={health.now}
            detail={
              health.userProfiles
                ? `${health.userProfiles.knownUsers.toLocaleString()} people known, ${health.userProfiles.neverRefreshed.toLocaleString()} never refreshed, ${health.userProfiles.notFound.toLocaleString()} no longer on VRChat. ${health.userProfiles.waiting.toLocaleString()} waiting right now (${waitingByReason(health.userProfiles.waitingByReason)}); ${health.userProfiles.refreshesInLastHour.toLocaleString()} refreshed in the last hour.` +
                  (health.userProfiles.oldestRefreshedAt
                    ? ` Oldest profile: refreshed ${ago(health.userProfiles.oldestRefreshedAt, health.now)}.`
                    : '') +
                  (health.userProfiles.lastRateLimitedAt
                    ? ` Last rate limited ${ago(health.userProfiles.lastRateLimitedAt, health.now)}.`
                    : '')
                : 'Fetches one profile at a time on the users lane, people seen in an instance first.'
            }
            run={health.lastUserProfileRun}
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

/** The queue's tiers in the design's order, highest first, with plain names. */
const REASON_LABEL: [key: string, label: string][] = [
  ['SeenInInstance', 'in an instance'],
  ['OpenedInModbot', 'opened in Modbot'],
  ['SeenInFactLog', 'seen in the log'],
  ['ProfileIsOld', 'old'],
  ['NeverRefreshed', 'never refreshed'],
]

function waitingByReason(counts: Record<string, number>): string {
  const parts = REASON_LABEL.filter(([key]) => (counts[key] ?? 0) > 0).map(
    ([key, label]) => `${counts[key]} ${label}`,
  )
  return parts.length > 0 ? parts.join(', ') : 'nobody'
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

const BOT_STATE: Record<DiscordBotHealth['state'], { label: string; tone: 'ok' | 'warn' | 'problem' | 'muted' }> = {
  NotConfigured: { label: 'not set up', tone: 'muted' },
  Connecting: { label: 'connecting', tone: 'warn' },
  Connected: { label: 'connected', tone: 'ok' },
  Disconnected: { label: 'reconnecting', tone: 'warn' },
  Failed: { label: 'stopped — needs you', tone: 'problem' },
}

const BOT_TONE: Record<'ok' | 'warn' | 'problem' | 'muted', string> = {
  ok: 'text-ok',
  warn: 'text-warn',
  problem: 'text-destructive',
  muted: 'text-muted-foreground',
}

/**
 * The Discord bot (foundation §9). "Not set up" is not a fault: no token is stored and nothing
 * else about Modbot is affected. "Stopped" means Discord refused the token or the intents, and
 * the bot waits for the settings to change rather than knocking every thirty seconds.
 */
function DiscordBot({ bot, now }: { bot: DiscordBotHealth; now: string }) {
  const state = BOT_STATE[bot.state]

  return (
    <Card>
      <CardContent className="py-4">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium">Discord bot</span>
          <span className={BOT_TONE[state.tone]} style={{ fontSize: 'var(--text-small)' }}>
            {state.label}
          </span>
          {bot.state === 'Connected' && bot.connectedSince && (
            <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              since {ago(bot.connectedSince, now)} · {bot.commandsRegistered} slash commands registered
            </span>
          )}
        </div>
        <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {bot.state === 'NotConfigured'
            ? 'No bot token or guild id is stored, so the bot is not running. Add them under Settings → Integrations to turn it on.'
            : bot.logChannelConfigured
              ? `Moderation events are posted to the log channel: ${bot.postedInThisProcess} since this process started${bot.lastPostedAt ? `, the last ${ago(bot.lastPostedAt, now)}` : ''}.`
              : 'No moderation log channel is set, so the bot answers commands and posts nothing.'}
        </p>
        {bot.lastError && (
          <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Last problem{bot.lastErrorAt ? ` (${ago(bot.lastErrorAt, now)})` : ''}: {bot.lastError}
          </p>
        )}
      </CardContent>
    </Card>
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
