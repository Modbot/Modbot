import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { statusOf, TONE } from '@/lib/gate'
import { ago, duration, formatDay } from '@/lib/format'
import { amountText, share } from '@/lib/aiSpend'
import {
  api,
  ApiError,
  type AiSpendWarning,
  type DiscordBotHealth,
  type DiscordChannelProblem,
  type DiscordReadBackHealth,
  type CalendarHealth,
  type EmailHealth,
  type SyncHealth,
} from '@/lib/api'
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
              ? 'You do not have permission to read sync health.'
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
                  Next probe no earlier than {new Date(health.gate.coldStopEndsAt).toLocaleTimeString()}
                </p>
              )}
              <dl
                className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-muted-foreground"
                style={{ fontSize: 'var(--text-small)' }}
              >
                <dt>Last signed in</dt>
                <dd className="text-foreground">
                  {health.gate.lastSignedInAt ? new Date(health.gate.lastSignedInAt).toLocaleString() : 'Never'}
                </dd>
                <dt>Sign-ins this hour</dt>
                <dd className="tabular-nums text-foreground">
                  {health.gate.signInsInLastHour} of {health.gate.signInLimit}
                </dd>
                {health.gate.signInWait && (
                  <>
                    <dt>Next sign-in</dt>
                    <dd className="text-destructive">
                      {new Date(health.gate.signInWait.retryAt).toLocaleTimeString()}
                    </dd>
                  </>
                )}
              </dl>
            </div>
          </div>
        </CardContent>
      </Card>

      {!health.syncRunningInThisProcess && <Note>Sync is not running in this process.</Note>}

      {health.aiSpend && health.aiSpend.length > 0 && <AiSpend warnings={health.aiSpend} />}

      {health.email && (health.email.queued > 0 || health.email.failed > 0) && <EmailQueue email={health.email} />}

      {health.discordBot && (
        <DiscordBot
          bot={health.discordBot}
          channels={health.discordChannelProblems ?? []}
          readBack={health.discordReadBack}
          missingManageEvents={health.calendar?.missingManageEvents ?? false}
          now={health.now}
        />
      )}

      {health.calendar && health.calendar.problems.length > 0 && (
        <CalendarProblems calendar={health.calendar} now={health.now} />
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
                : undefined
            }
            run={health.lastAuditLogRun}
          />

          <Producer
            name="Group info"
            polledAt={health.groupInfoPolledAt}
            now={health.now}
            run={health.lastGroupInfoRun}
          />

          <Producer
            name="User profiles"
            polledAt={health.userProfilePolledAt}
            now={health.now}
            detail={health.userProfiles ? profileDetail(health.userProfiles, health.now) : undefined}
            run={health.lastUserProfileRun}
          />

          <Producer
            name="Member list"
            polledAt={health.memberSweep?.polledAt ?? null}
            now={health.now}
            detail={sweepDetail(health.memberSweep, health.now, 'members')}
            run={health.memberSweep?.lastRun ?? null}
          />

          <Producer
            name="Ban list"
            polledAt={health.banSweep?.polledAt ?? null}
            now={health.now}
            detail={sweepDetail(health.banSweep, health.now, 'bans')}
            run={health.banSweep?.lastRun ?? null}
          />

          <p className="mt-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {health.auditLogCatchUpComplete
              ? health.auditLogHistoryHorizon
                ? `Audit log catch-up finished · ${health.auditLogHistoryHorizon.entriesRead} entries read`
                : 'Audit log catch-up finished'
              : 'Audit log catch-up running'}
            {health.auditLogSyncedThrough && ` · synced through ${formatDay(health.auditLogSyncedThrough)}`}
          </p>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="py-4">
          <div className="mb-3 font-medium">Rate-limit budgets</div>

          {health.buckets.length === 0 ? (
            <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              None used yet.
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
              None seen.
            </p>
          ) : (
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

/**
 * One line for a sweep. The phase comes first because it is the thing "last ran 9 minutes
 * ago" cannot say: resting for fifteen minutes on purpose and stuck look the same from the age.
 */
function sweepDetail(sweep: SyncHealth['memberSweep'], now: string, what: 'members' | 'bans'): string {
  if (!sweep) return 'Not running'

  const pages = (n: number) => `${n} ${n === 1 ? 'page' : 'pages'}`

  const phase =
    sweep.coldStopped
      ? 'Cold-stopped'
      : sweep.phase === 'sweeping'
        ? `Sweeping · ${pages(sweep.pagesWalked)} read · offset ${sweep.offset.toLocaleString()}`
        : sweep.phase === 'resting'
          ? 'Resting'
          : sweep.phase === 'retrying'
            ? 'Retrying'
            : sweep.phase === 'idle'
              ? 'Idle'
              : sweep.phase

  const last = sweep.lastCompletedAt
    ? `last full sweep ${ago(sweep.lastCompletedAt, now)}: ${sweep.count.toLocaleString()} ${what}${sweep.phase !== 'sweeping' ? `, ${pages(sweep.pagesWalked)}` : ''}, ${sweep.rowsChanged.toLocaleString()} ${sweep.rowsChanged === 1 ? 'row' : 'rows'} changed`
    : 'no full sweep yet'

  // Counted since this process started. "Already in the audit log" is a change the list saw that
  // the audit log had recorded first, so the list left it alone.
  const facts = `${sweep.factsWritten.toLocaleString()} changes recorded · ${sweep.factsDeduplicated.toLocaleString()} already in the audit log`

  const next = sweep.nextPassAt ? `next pass ${nextAt(sweep.nextPassAt, now)}` : null

  return [phase, last, facts, next].filter(Boolean).join(' · ')
}

/** The profile queue's numbers, as one line. */
function profileDetail(p: NonNullable<SyncHealth['userProfiles']>, now: string): string {
  const reasons = waitingByReason(p.waitingByReason)

  return [
    `${p.knownUsers.toLocaleString()} known`,
    `${p.neverRefreshed.toLocaleString()} never refreshed`,
    `${p.notFound.toLocaleString()} no longer on VRChat`,
    `${p.waiting.toLocaleString()} waiting${reasons ? ` (${reasons})` : ''}`,
    `${p.refreshesInLastHour.toLocaleString()} refreshed in the last hour`,
    p.oldestRefreshedAt ? `oldest refreshed ${ago(p.oldestRefreshedAt, now)}` : null,
    p.lastRateLimitedAt ? `last rate limited ${ago(p.lastRateLimitedAt, now)}` : null,
  ]
    .filter(Boolean)
    .join(' · ')
}

/** "in 12 minutes" or "any moment now", against the server's clock. */
function nextAt(iso: string, now: string): string {
  const seconds = Math.round((Date.parse(iso) - Date.parse(now)) / 1000)
  if (seconds <= 5) return 'any moment now'
  if (seconds < 60) return `in ${seconds} seconds`
  if (seconds < 3600) return `in ${Math.round(seconds / 60)} minutes`
  return `in ${(seconds / 3600).toFixed(1)} hours`
}

function waitingByReason(counts: Record<string, number>): string {
  const parts = REASON_LABEL.filter(([key]) => (counts[key] ?? 0) > 0).map(
    ([key, label]) => `${counts[key]} ${label}`,
  )
  return parts.join(', ')
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
  detail?: string
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
      {detail && (
        <p className="mt-0.5 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {detail}
        </p>
      )}
      {run && (
        <p className="mt-0.5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Last run: {run.outcome.toLowerCase()} — {run.summary} ({run.durationSeconds.toFixed(1)}s,{' '}
          {ago(run.at, now)})
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
const CALENDAR_PLACE: Record<string, string> = {
  vrchat: 'VRChat calendar',
  discordEvent: 'Discord event',
  channelPost: 'Channel post',
  instance: 'Instance',
}

/** Calendar events that did not publish or whose instance did not open (calendar design §3, §4). */
function CalendarProblems({ calendar, now }: { calendar: CalendarHealth; now: string }) {
  return (
    <Card>
      <CardContent className="py-4">
        <div className="font-medium">Calendar</div>
        {calendar.problems.map((p) => (
          <p
            key={`${p.eventId}-${p.place}`}
            className="mt-1 max-w-3xl text-warn"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {p.title} · {CALENDAR_PLACE[p.place] ?? p.place} · {p.error}
            {p.at ? ` (${ago(p.at, now)})` : ''}
          </p>
        ))}
      </CardContent>
    </Card>
  )
}

function DiscordBot({
  bot,
  channels,
  readBack,
  missingManageEvents,
  now,
}: {
  bot: DiscordBotHealth
  channels: DiscordChannelProblem[]
  readBack: DiscordReadBackHealth | null
  missingManageEvents: boolean
  now: string
}) {
  // Not BOT_STATE[bot.state] directly. That Record is a compile-time claim about a value which
  // arrives over HTTP, and the two part company whenever the server sends something this build
  // does not know -- an added state, or an enum written as its number. A miss returned undefined
  // and the next line read `.tone` off it, which threw during render and took the whole screen
  // down over one card. Exactly the failure `statusOf` in lib/gate.ts was hardened against.
  const state = BOT_STATE[bot.state] ?? {
    label: bot.state ? `unknown (${String(bot.state)})` : 'unknown',
    tone: 'muted' as const,
  }

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
        {bot.state !== 'NotConfigured' && (
          <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {`Posted to Discord: ${bot.postedInThisProcess}${bot.lastPostedAt ? `, last ${ago(bot.lastPostedAt, now)}` : ''}`}
            {!bot.logChannelConfigured && ' · No channels set'}
          </p>
        )}
        {channels.map((channel) => (
          <p
            key={channel.channelId}
            className="mt-1 max-w-3xl text-warn"
            style={{ fontSize: 'var(--text-small)' }}
          >
            {channel.name ? `#${channel.name}` : channel.channelId}
            {channel.removed && ' · Removed'}
            {channel.missing.length > 0 && ` · Missing ${channel.missing.join(', ')}`}
            {channel.lastError &&
              ` · ${channel.lastError}${channel.lastErrorAt ? ` (${ago(channel.lastErrorAt, now)})` : ''}`}
          </p>
        ))}
        {missingManageEvents && (
          <p className="mt-1 max-w-3xl text-warn" style={{ fontSize: 'var(--text-small)' }}>
            Missing Manage Events
          </p>
        )}
        {bot.missingIntents && bot.missingIntents.length > 0 && (
          <p className="mt-1 max-w-3xl text-destructive" style={{ fontSize: 'var(--text-small)' }}>
            Intents off in the Developer Portal: {bot.missingIntents.join(', ')}
          </p>
        )}
        {readBack && readBack.channels > 0 && (
          <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {`Message history read back: ${readBack.finished.toLocaleString()} of ${readBack.channels.toLocaleString()} channels and threads · ${readBack.messagesStored.toLocaleString()} messages stored`}
            {readBack.noAccess > 0 && ` · ${readBack.noAccess.toLocaleString()} without access`}
          </p>
        )}
        {readBack?.lastError && (
          <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Last reading problem{readBack.lastErrorAt ? ` (${ago(readBack.lastErrorAt, now)})` : ''}: {readBack.lastError}
          </p>
        )}
        {bot.lastError && (
          <p className="mt-1 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            Last problem{bot.lastErrorAt ? ` (${ago(bot.lastErrorAt, now)})` : ''}: {bot.lastError}
          </p>
        )}
      </CardContent>
    </Card>
  )
}

/**
 * AI spend limits for everyone or a feature that are at 80%, estimated to be passed this month, or
 * reached (AI chat design §10.7). Shown only when there is one, in the Discord bot card's line
 * format: amber while close, red once reached.
 */
function AiSpend({ warnings }: { warnings: AiSpendWarning[] }) {
  const reached = warnings.some((w) => w.reached)

  return (
    <Card>
      <CardContent className="py-4">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium">AI spend</span>
          <span className={reached ? 'text-destructive' : 'text-warn'} style={{ fontSize: 'var(--text-small)' }}>
            {reached ? 'limit reached' : 'close to a limit'}
          </span>
        </div>
        {warnings.map((w) => (
          <p
            key={`${w.appliesTo}:${w.feature ?? ''}:${w.period}`}
            className={cn('mt-1 max-w-3xl tabular-nums', w.reached ? 'text-destructive' : 'text-warn')}
            style={{ fontSize: 'var(--text-small)' }}
          >
            {w.appliesTo === 'everyone' ? 'Everyone' : (w.label ?? w.feature)}
            {` · ${w.period === 'day' ? 'daily' : 'monthly'} ${w.unit === 'tokens' ? 'token ' : ''}limit`}
            {` · ${amountText(w.spent, w.unit)} of ${amountText(w.limit, w.unit)} (${share(w.spent, w.limit)})`}
            {w.partUnknown && ' + unknown'}
            {w.estimate !== null && ` · estimate ${amountText(w.estimate, w.unit)} (${share(w.estimate, w.limit)})`}
          </p>
        ))}
      </CardContent>
    </Card>
  )
}

function EmailQueue({ email }: { email: EmailHealth }) {
  return (
    <Card>
      <CardContent className="py-4">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="font-medium">Email</span>
          <span className={email.failed > 0 ? 'text-destructive' : 'text-warn'} style={{ fontSize: 'var(--text-small)' }}>
            {email.failed > 0 ? 'emails failed' : 'emails queued'}
          </span>
        </div>
        <p className="mt-1 max-w-3xl tabular-nums text-warn" style={{ fontSize: 'var(--text-small)' }}>
          {`${email.queued} queued`}
          {email.nextSendAt && ` · next at ${new Date(email.nextSendAt).toLocaleString()}`}
          {email.failed > 0 && ` · ${email.failed} failed`}
        </p>
      </CardContent>
    </Card>
  )
}

function Note({ children }: { children: React.ReactNode }) {
  return (
    <div
      className="rounded-xl border border-warn/40 bg-warn/10 px-4 py-3 text-muted-foreground"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      {children}
    </div>
  )
}
