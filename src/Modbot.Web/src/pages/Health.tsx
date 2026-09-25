import { Children, useEffect, useState } from 'react'
import { AlertsCard } from '@/components/alerts/AlertsCard'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Row } from '@/components/settings/fields'
import { Card, CardAction, CardContent, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { statusOf } from '@/lib/gate'
import { discordState, DOT, TONE, type Tone } from '@/lib/status'
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
  type AiCallsHealth,
  type DemoStatus,
  type EmailHealth,
  type LogHealth,
  type PausedRule,
  type SyncHealth,
} from '@/lib/api'
import { cn } from '@/lib/utils'
import { Empty } from './Members'

/**
 * What the gate and the producers would tell an operator about themselves (spec 4.2.3, 4.3.3).
 *
 * The screen is ordered by how much somebody has to care, not by subsystem: whether VRChat is
 * reachable, then whether the producers are running, then the budgets, then the event types
 * Modbot has seen and not understood.
 */

export function Health() {
  const [health, setHealth] = useState<SyncHealth | null>(null)
  const [databaseReachable, setDatabaseReachable] = useState<boolean | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    const load = () => {
      api
        .databaseHealth()
        .then((ok) => !cancelled && setDatabaseReachable(ok))
        .catch(() => !cancelled && setDatabaseReachable(null))

      return api
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
    }

    void load()

    // Polled rather than pushed. The numbers move on the producers' own schedule, and a screen
    // somebody leaves open should not go stale into a decision.
    const timer = setInterval(() => void load(), 20_000)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  const loaded = health !== null

  // The status rows at the foot of the sidebar open this page at one part: `/health#discord`.
  // Waiting for the answer matters -- before it arrives the card the address names is not drawn
  // yet, and a scroll to nothing leaves the operator at the top of the page wondering.
  useEffect(() => {
    const scroll = () => {
      const id = window.location.hash.slice(1)
      if (id) document.getElementById(id)?.scrollIntoView({ block: 'start' })
    }

    if (loaded) scroll()

    window.addEventListener('hashchange', scroll)
    window.addEventListener('popstate', scroll)

    return () => {
      window.removeEventListener('hashchange', scroll)
      window.removeEventListener('popstate', scroll)
    }
  }, [loaded])

  if (error) return <Empty>{error}</Empty>
  if (!health) return <Empty>Loading…</Empty>

  const status = statusOf(health.gate.status)

  return (
    <PanelGrid className="grid-cols-1">
      <DemoProgress />

      <AlertsCard />

      <Part
        id="vrchat"
        title="VRChat"
        state={<State tone={status.tone}>{status.label}</State>}
        aside={
          <>
            gate state: <span className="font-mono">{health.gate.state}</span>
          </>
        }
      >
        <p className="max-w-3xl">{health.gate.headline}</p>
        {health.gate.coldStopEndsAt && (
          <p>
            Next probe no earlier than{' '}
            <span className="font-mono">{new Date(health.gate.coldStopEndsAt).toLocaleTimeString()}</span>
          </p>
        )}
        {/* Row's value takes its colour from around it and its label stays muted, so the wrapper
            sets the value's tone. */}
        <div className="mt-1 max-w-xs text-foreground">
          <Row
            label="Last signed in"
            value={health.gate.lastSignedInAt ? new Date(health.gate.lastSignedInAt).toLocaleString() : 'Never'}
            mono={!!health.gate.lastSignedInAt}
          />
          <Row label="Sign-ins this hour" value={`${health.gate.signInsInLastHour} of ${health.gate.signInLimit}`} mono />
          {health.gate.signInWait && (
            <div className="text-destructive">
              <Row label="Next sign-in" value={new Date(health.gate.signInWait.retryAt).toLocaleTimeString()} mono />
            </div>
          )}
        </div>
      </Part>

      <Database reachable={databaseReachable} />

      {/* One anchor over both AI panels, because either can be the only one on the screen. It is a
          row of the sheet rather than a panel, so the two draw the sheet's lines and no box of
          their own. */}
      <PanelGrid id="ai" className="scroll-mt-20 grid-cols-1 empty:hidden">
        {health.aiSpend && health.aiSpend.length > 0 && <AiSpend warnings={health.aiSpend} />}

        {health.aiCalls &&
          (health.aiCalls.errors > 0 || health.aiCalls.timedOut > 0 || health.aiCalls.fallbacks > 0) && (
            <AiCalls calls={health.aiCalls} />
          )}
      </PanelGrid>

      {health.email && (health.email.queued > 0 || health.email.failed > 0) && <EmailQueue email={health.email} />}
      {health.logs && <Logs logs={health.logs} />}

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

      {health.pausedRules && health.pausedRules.length > 0 && (
        <PausedRules rules={health.pausedRules} now={health.now} />
      )}

      <Card id="sync" className="scroll-mt-20">
        {/* The warning sits on the producers' own strip, since theirs are the passes not being
            made here. */}
        <CardHeader className={cn(!health.syncRunningInThisProcess && 'bg-warn/10')}>
          <CardTitle>Producers</CardTitle>
          {!health.syncRunningInThisProcess && (
            <CardAction className="font-medium" style={{ fontSize: 'var(--text-small)' }}>
              <span aria-hidden className="size-2 shrink-0 bg-warn" />
              Sync is not running in this process.
            </CardAction>
          )}
        </CardHeader>

        <div>
          <Producer
            name="Group audit log"
            polledAt={health.auditLogPolledAt}
            now={health.now}
            detail={
              health.auditLogPollRate
                ? `Polling every ${duration(health.auditLogPollRate.intervalSeconds)}: ${health.auditLogPollRate.reason}`
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
            name="User details"
            polledAt={health.userProfilePolledAt}
            now={health.now}
            detail={health.userReads ? userReadDetail(health.userReads, health.now) : undefined}
            run={health.lastUserReadRun ?? null}
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
        </div>

        <CardFooter className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <p>
            {health.auditLogCatchUpComplete
              ? health.auditLogHistoryHorizon
                ? `Audit log catch-up finished · ${health.auditLogHistoryHorizon.entriesRead} entries read`
                : 'Audit log catch-up finished'
              : 'Audit log catch-up running'}
            {health.auditLogSyncedThrough && ` · synced through ${formatDay(health.auditLogSyncedThrough)}`}
          </p>
        </CardFooter>
      </Card>

      {health.cloudReport && <CloudReport report={health.cloudReport} now={health.now} />}

      <Card>
        <CardHeader>
          <CardTitle>Rate-limit budgets</CardTitle>
        </CardHeader>

        {health.buckets.length === 0 ? (
          <EmptyRow>None used yet.</EmptyRow>
        ) : (
          <div data-pin-first className="relative overflow-x-auto">
            <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
              <thead className="bg-strip text-muted-foreground">
                <tr className="border-b-(length:--hairline)">
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Bucket</th>
                  <th className="px-3 py-2 text-right font-normal whitespace-nowrap">Rate (req/s)</th>
                  <th className="px-3 py-2 text-right font-normal whitespace-nowrap">Budget</th>
                  <th className="px-3 py-2 text-right font-normal whitespace-nowrap">429s</th>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">State</th>
                </tr>
              </thead>
              <tbody>
                {health.buckets.map((bucket) => (
                  <tr key={bucket.name} className="border-b border-b-(length:--hairline) last:border-b-0">
                    <td className="px-3 font-mono" style={{ height: 'var(--row-h)' }}>
                      {bucket.name}
                    </td>
                    <td className="px-3 text-right font-mono">{bucket.effectiveRatePerSecond.toFixed(3)}</td>
                    <td className={cn('px-3 text-right font-mono', bucket.budgetMultiplier < 1 && 'text-warn')}>
                      {bucket.budgetMultiplier.toFixed(2)}×
                    </td>
                    <td className="px-3 text-right font-mono">{bucket.rateLimitHits}</td>
                    <td className="px-3 whitespace-nowrap">
                      {bucket.alerting ? (
                        <State tone="bad">given up, needs you</State>
                      ) : bucket.isColdStopped ? (
                        <State tone="warn">
                          cold-stopped
                          {bucket.stoppedUntil && (
                            <>
                              {' until '}
                              <span className="font-mono">{new Date(bucket.stoppedUntil).toLocaleTimeString()}</span>
                            </>
                          )}
                        </State>
                      ) : (
                        <State tone="muted">running</State>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Audit-log event types Modbot does not understand</CardTitle>
        </CardHeader>

        {health.unmappedAuditEvents.length === 0 ? (
          <EmptyRow>None seen.</EmptyRow>
        ) : (
          <div className="relative overflow-x-auto">
            <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
              <tbody>
                {health.unmappedAuditEvents.map((e) => (
                  <tr key={e.eventType} className="border-b border-b-(length:--hairline) last:border-b-0">
                    <td className="px-3 font-mono whitespace-nowrap" style={{ height: 'var(--row-h)' }}>
                      {e.eventType}
                    </td>
                    <td className="px-3 text-right font-mono text-muted-foreground">{e.count}×</td>
                    <td className="w-full px-3 text-muted-foreground">{e.sampleDescription ?? e.sampleEntryId ?? ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </PanelGrid>
  )
}

/**
 * One part of the sheet: its name on the strip with its state beside it and any aside on the
 * right, and the lines about it below. A part with nothing more to say is the strip alone, and
 * then the strip draws no line of its own under it, since the sheet already draws one there.
 */
function Part({
  id,
  title,
  state,
  aside,
  children,
}: {
  id?: string
  title: string
  state?: React.ReactNode
  aside?: React.ReactNode
  children?: React.ReactNode
}) {
  const body = Children.toArray(children).length > 0

  return (
    <Card id={id} className={id ? 'scroll-mt-20' : undefined}>
      <CardHeader className={body ? undefined : 'border-b-0'}>
        <CardTitle>{title}</CardTitle>
        {state}
        {aside && (
          <CardAction className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span>{aside}</span>
          </CardAction>
        )}
      </CardHeader>
      {body && (
        <CardContent className="flex flex-col gap-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {children}
        </CardContent>
      )}
    </Card>
  )
}

/** A state in plain words, led by the square in its tone. The words carry it; the square is a glance. */
function State({ tone, children }: { tone: Tone; children: React.ReactNode }) {
  return (
    <span className={cn('inline-flex items-center gap-1.5', TONE[tone])} style={{ fontSize: 'var(--text-small)' }}>
      <span aria-hidden className={cn('size-2 shrink-0', DOT[tone])} />
      {children}
    </span>
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

/** The rarer read's numbers, as one line. */
function userReadDetail(u: NonNullable<SyncHealth['userReads']>, now: string): string {
  return [
    `${u.neverRead.toLocaleString()} never read`,
    `${u.readsInLastHour.toLocaleString()} read in the last hour`,
    u.oldestReadAt ? `oldest read ${ago(u.oldestReadAt, now)}` : null,
    u.lastRateLimitedAt ? `last rate limited ${ago(u.lastRateLimitedAt, now)}` : null,
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

function CloudReport({
  report,
  now,
}: {
  report: NonNullable<SyncHealth['cloudReport']>
  now: string
}) {
  const state = report.sentAt === null ? 'Not sent yet' : report.ok ? 'Sent' : 'Failed'
  const tone: Tone = report.sentAt === null ? 'muted' : report.ok ? 'ok' : 'bad'

  return (
    <Part
      title="Modbot Cloud"
      state={<State tone={tone}>{state}</State>}
      aside={report.sentAt === null ? report.endpoint : `${ago(report.sentAt, now)} · ${report.endpoint}`}
    >
      {report.problem && <p className="max-w-3xl">{report.problem}</p>}
    </Part>
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
  detail?: string
  run: SyncHealth['lastAuditLogRun']
}) {
  return (
    <div className="border-b border-b-(length:--hairline) px-(--panel-pad) py-2 last:border-b-0">
      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="font-medium">{name}</span>
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          last completed a pass <span className="font-mono">{ago(polledAt, now)}</span>
        </span>
      </div>
      {detail && (
        <p className="mt-0.5 max-w-3xl text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {detail}
        </p>
      )}
      {run && (
        <p className="mt-0.5 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Last run: {run.outcome.toLowerCase()}. {run.summary} ({run.durationSeconds.toFixed(1)}s,{' '}
          {ago(run.at, now)})
        </p>
      )}
    </div>
  )
}

/**
 * Whether Modbot can reach its database, from the same readiness probe a hosting platform calls.
 */
function Database({ reachable }: { reachable: boolean | null }) {
  const state =
    reachable === null
      ? { label: 'unknown', tone: 'muted' as const }
      : reachable
        ? { label: 'online', tone: 'ok' as const }
        : { label: 'unreachable', tone: 'bad' as const }

  return <Part id="database" title="Database" state={<State tone={state.tone}>{state.label}</State>} />
}

const CALENDAR_PLACE: Record<string, string> = {
  vrchat: 'VRChat calendar',
  discordEvent: 'Discord event',
  channelPost: 'Channel post',
  instance: 'Instance',
}

/**
 * Moderation rules that stopped themselves (AI moderation design §13.2).
 *
 * A paused rule keeps flagging and quietly stops acting, so nothing else on this screen would say
 * that what the operator asked for is not happening.
 */
function PausedRules({ rules, now }: { rules: PausedRule[]; now: string }) {
  return (
    <Part title="Paused moderation rules">
      {rules.map((r) => (
        <p key={r.ruleId} className="max-w-3xl text-warn">
          {r.ruleName} · {r.reason ?? 'Paused'} ({ago(r.pausedAt, now)})
        </p>
      ))}
    </Part>
  )
}

/** Calendar events that did not publish or whose instance did not open (calendar design §3, §4). */
function CalendarProblems({ calendar, now }: { calendar: CalendarHealth; now: string }) {
  return (
    <Part title="Calendar">
      {calendar.problems.map((p) => (
        <p key={`${p.eventId}-${p.place}`} className="max-w-3xl text-warn">
          {p.title} · {CALENDAR_PLACE[p.place] ?? p.place} · {p.error}
          {p.at ? ` (${ago(p.at, now)})` : ''}
        </p>
      ))}
    </Part>
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
  // Looked up through `discordState` rather than in a Record here. That Record would be a
  // compile-time claim about a value which arrives over HTTP, and the two part company whenever
  // the server sends something this build does not know -- an added state, or an enum written as
  // its number. A miss returns undefined and the next line reads `.tone` off it, which throws
  // during render and takes the whole screen down over one card.
  const state = discordState(bot.state)

  return (
    <Part
      id="discord"
      title="Discord bot"
      state={<State tone={state.tone}>{state.label}</State>}
      aside={
        bot.state === 'Connected' &&
        bot.connectedSince &&
        `since ${ago(bot.connectedSince, now)} · ${bot.commandsRegistered} slash commands registered`
      }
    >
      {bot.state !== 'NotConfigured' && (
        <p className="max-w-3xl">
          {`Posted to Discord: ${bot.postedInThisProcess}${bot.lastPostedAt ? `, last ${ago(bot.lastPostedAt, now)}` : ''}`}
          {!bot.logChannelConfigured && ' · No channels set'}
        </p>
      )}
      {channels.map((channel) => (
        <p key={channel.channelId} className="max-w-3xl text-warn">
          {channel.name ? `#${channel.name}` : channel.channelId}
          {channel.removed && ' · Removed'}
          {channel.missing.length > 0 && ` · Missing ${channel.missing.join(', ')}`}
          {channel.lastError &&
            ` · ${channel.lastError}${channel.lastErrorAt ? ` (${ago(channel.lastErrorAt, now)})` : ''}`}
        </p>
      ))}
      {missingManageEvents && <p className="max-w-3xl text-warn">Missing Manage Events</p>}
      {bot.missingIntents && bot.missingIntents.length > 0 && (
        <p className="max-w-3xl text-destructive">
          Intents off in the Developer Portal: {bot.missingIntents.join(', ')}
        </p>
      )}
      {readBack && readBack.channels > 0 && (
        <p className="max-w-3xl">
          {`Message history read back: ${readBack.finished.toLocaleString()} of ${readBack.channels.toLocaleString()} channels and threads · ${readBack.messagesStored.toLocaleString()} messages stored`}
          {readBack.noAccess > 0 && ` · ${readBack.noAccess.toLocaleString()} without access`}
        </p>
      )}
      {readBack?.lastError && (
        <p className="max-w-3xl">
          Last reading problem{readBack.lastErrorAt ? ` (${ago(readBack.lastErrorAt, now)})` : ''}: {readBack.lastError}
        </p>
      )}
      {bot.lastError && (
        <p className="max-w-3xl">
          Last problem{bot.lastErrorAt ? ` (${ago(bot.lastErrorAt, now)})` : ''}: {bot.lastError}
        </p>
      )}
    </Part>
  )
}

/**
 * How far the demo's data has got. Nothing on a deployment that is not a demo, and nothing on a
 * demo whose data is already complete.
 */
function DemoProgress() {
  const [demo, setDemo] = useState<DemoStatus | null>(null)

  useEffect(() => {
    let cancelled = false

    const load = () =>
      api
        .demoStatus()
        .then((next) => {
          if (!cancelled) setDemo(next)
        })
        .catch(() => undefined)

    void load()

    const timer = setInterval(() => void load(), 3000)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  if (!demo?.on || !demo.busy) return null

  return (
    <Part
      title="Demo data"
      state={
        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {demo.step}
          {demo.total > 0 ? ` — ${demo.done.toLocaleString()} of ${demo.total.toLocaleString()}` : ''}
        </span>
      }
    />
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
    <Part
      title="AI spend"
      state={<State tone={reached ? 'bad' : 'warn'}>{reached ? 'limit reached' : 'close to a limit'}</State>}
    >
      {warnings.map((w) => (
        <p
          key={`${w.appliesTo}:${w.feature ?? ''}:${w.period}`}
          className={cn('max-w-3xl tabular-nums', w.reached ? 'text-destructive' : 'text-warn')}
        >
          {w.appliesTo === 'everyone' ? 'Everyone' : (w.label ?? w.feature)}
          {` · ${w.period === 'day' ? 'daily' : 'monthly'} ${w.unit === 'tokens' ? 'token ' : ''}limit`}
          {` · ${amountText(w.spent, w.unit)} of ${amountText(w.limit, w.unit)} (${share(w.spent, w.limit)})`}
          {w.partUnknown && ' + unknown'}
          {w.estimate !== null && ` · estimate ${amountText(w.estimate, w.unit)} (${share(w.estimate, w.limit)})`}
        </p>
      ))}
    </Part>
  )
}

/**
 * AI calls over the last hour: how many failed, how many ran out of time, and which model is
 * answering while the fallback is in use. Shown only when there is something wrong.
 */
function AiCalls({ calls }: { calls: AiCallsHealth }) {
  const failing = calls.errors + calls.timedOut > 0

  return (
    <Part
      title="AI"
      state={<State tone={failing ? 'bad' : 'warn'}>{failing ? 'calls failing' : 'on the fallback model'}</State>}
    >
      <p className={cn('max-w-3xl tabular-nums', failing ? 'text-destructive' : 'text-warn')}>
        {`${calls.calls} calls in the last hour`}
        {calls.errors > 0 && ` · ${calls.errors} failed`}
        {calls.timedOut > 0 && ` · ${calls.timedOut} timed out`}
        {calls.fallbacks > 0 && ` · ${calls.fallbacks} on the fallback`}
        {calls.answeringModel && ` · answering: ${calls.answeringModel}`}
      </p>
    </Part>
  )
}

function EmailQueue({ email }: { email: EmailHealth }) {
  return (
    <Part
      title="Email"
      state={
        <State tone={email.failed > 0 ? 'bad' : 'warn'}>{email.failed > 0 ? 'emails failed' : 'emails queued'}</State>
      }
    >
      <p className="max-w-3xl tabular-nums text-warn">
        {`${email.queued} queued`}
        {email.nextSendAt && ` · next at ${new Date(email.nextSendAt).toLocaleString()}`}
        {email.failed > 0 && ` · ${email.failed} failed`}
      </p>
    </Part>
  )
}

function Logs({ logs }: { logs: LogHealth }) {
  const storeProblem = logs.storeError !== null || logs.storedDropped > 0
  const cloudProblem = logs.sendingToCloud && (logs.cloudError !== null || logs.cloudDropped > 0)

  return (
    <Part title="Logs">
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
        <dt>Stored</dt>
        <dd className={cn('tabular-nums', storeProblem ? 'text-warn' : 'text-foreground')}>
          {logs.storing ? `${logs.storedWritten.toLocaleString()} written` : 'Not writing'}
          {logs.storedDropped > 0 && ` · ${logs.storedDropped.toLocaleString()} dropped`}
          {logs.storeError && ` · ${logs.storeError}`}
        </dd>

        <dt>To Modbot Cloud</dt>
        <dd className={cn('tabular-nums', cloudProblem ? 'text-warn' : 'text-foreground')}>
          {!logs.cloudAllowed
            ? 'Off'
            : !logs.sendingToCloud
              ? 'Off'
              : logs.cloudSentAt
                ? `Last sent ${new Date(logs.cloudSentAt).toLocaleString()}`
                : logs.cloudRegistered
                  ? 'Nothing sent yet'
                  : 'Not registered yet'}
          {logs.sendingToCloud && logs.cloudWaiting > 0 && ` · ${logs.cloudWaiting.toLocaleString()} waiting`}
          {logs.cloudDropped > 0 && ` · ${logs.cloudDropped.toLocaleString()} dropped`}
          {logs.sendingToCloud && logs.cloudError && ` · ${logs.cloudError}`}
        </dd>
      </dl>
    </Part>
  )
}
