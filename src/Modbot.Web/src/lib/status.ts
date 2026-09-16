import type { DiscordBotHealth, GateStatus, SyncHealth } from '@/lib/api'

/**
 * What Modbot's parts would say about themselves, in one word each, and the colour that word gets.
 *
 * One table, read by the rows at the foot of the sidebar and by the Health page. The two must
 * never disagree about whether Modbot is broken, and the surest way to guarantee that is for both
 * to read the same table, with the state itself decided by the server rather than by either of
 * them.
 *
 * No icons here on purpose: the icon each state gets lives in `lib/gate.ts`, so this file stays
 * plain data that the tests can load without a rendering library behind it.
 */

export type Tone = 'ok' | 'warn' | 'bad' | 'muted'

export const TONE: Record<Tone, string> = {
  ok: 'text-ok',
  warn: 'text-warn',
  bad: 'text-destructive',
  muted: 'text-muted-foreground',
}

export const DOT: Record<Tone, string> = {
  ok: 'bg-ok',
  warn: 'bg-warn',
  bad: 'bg-destructive',
  muted: 'bg-muted-foreground',
}

export type State = { label: string; tone: Tone }

/** How each gate status is presented (spec 4.3.3). */
export const VRCHAT: Record<GateStatus, State> = {
  Working: { label: 'Working', tone: 'ok' },

  // Deliberately not an error tone. Waiting out a rate limit is spec 4.3.1 behaving correctly and
  // recovering on its own; painting it red trains an operator to intervene, and against a penalty
  // that grows on every probe, intervening is the one thing that makes it worse.
  WaitingOnPurpose: { label: 'Waiting on purpose', tone: 'warn' },

  // Broken. Identical to the line above from outside -- traffic stopped, data not arriving -- and
  // the opposite meaning: nothing changes until somebody does something.
  NeedsOperator: { label: 'Needs you', tone: 'bad' },

  NotConfigured: { label: 'Not configured', tone: 'muted' },
}

/**
 * The status table, but safe against a value the server knows and this build does not.
 *
 * `Record<GateStatus, …>` is a compile-time claim about a *runtime* value that arrives over
 * HTTP, and the two part company the moment a server is newer than the page holding a cached
 * bundle. A miss used to return `undefined`, and the caller read `.tone` off it — which threw
 * during render, and because the indicator sits in the app shell, it took down every screen in
 * Modbot rather than one badge.
 *
 * So an unknown status renders as unknown, which is both true and survivable.
 */
export function vrchatState(status: string | null | undefined): State {
  return VRCHAT[status as GateStatus] ?? unknownState(status)
}

/**
 * The Discord bot (foundation §9). "Not set up" is not a fault: no token is stored and nothing
 * else about Modbot is affected. "Stopped" means Discord refused the token or the intents, and
 * the bot waits for the settings to change rather than knocking every thirty seconds.
 */
export const DISCORD: Record<DiscordBotHealth['state'], State> = {
  NotConfigured: { label: 'not set up', tone: 'muted' },
  Connecting: { label: 'connecting', tone: 'warn' },
  Connected: { label: 'working', tone: 'ok' },
  Disconnected: { label: 'reconnecting', tone: 'warn' },
  Failed: { label: 'stopped, needs you', tone: 'bad' },
}

export function discordState(state: string | null | undefined): State {
  return DISCORD[state as DiscordBotHealth['state']] ?? unknownState(state)
}

function unknownState(value: string | null | undefined): State {
  return { label: value ? `unknown (${value})` : 'unknown', tone: 'muted' }
}

/** The rows at the foot of the sidebar. The id is the Health page section each one opens. */
export type StatusRowId = 'vrchat' | 'discord' | 'database' | 'sync' | 'ai'

export type StatusRow = { id: StatusRowId; name: string; state: string; tone: Tone }

export type StatusReading = {
  /** The gate's own poll, shared with the sign-in banner. Null while it has not answered. */
  gate: string | null
  /** The rest of what the Health page reads. Null while it has not answered. */
  health: SyncHealth | null
  /** Whether Modbot says it can reach its database (`/health/ready`). Null while unknown. */
  databaseReachable: boolean | null
}

/**
 * One row per part of Modbot, in the order somebody has to care about them.
 *
 * A part that has not answered reads "unknown" rather than green: a failed fetch shown as healthy
 * is the same lie in a quieter costume.
 *
 * AI only appears once this deployment is using it -- a spend warning, or calls in the last hour.
 * There is nothing to say about a feature that is switched off.
 */
export function statusRows({ gate, health, databaseReachable }: StatusReading): StatusRow[] {
  const rows: StatusRow[] = [
    row('vrchat', 'VRChat', gate === null ? unknownState(null) : vrchatState(gate)),
    row(
      'discord',
      'Discord',
      health === null
        ? unknownState(null)
        : health.discordBot === null
          ? DISCORD.NotConfigured
          : discordState(health.discordBot.state),
    ),
    row(
      'database',
      'Database',
      databaseReachable === null
        ? unknownState(null)
        : databaseReachable
          ? { label: 'online', tone: 'ok' }
          : { label: 'unreachable', tone: 'bad' },
    ),
    row(
      'sync',
      'Sync',
      health === null
        ? unknownState(null)
        : health.syncRunningInThisProcess
          ? { label: 'running', tone: 'ok' }
          : { label: 'stopped', tone: 'warn' },
    ),
  ]

  const ai = health && aiState(health)
  if (ai) rows.push(row('ai', 'AI', ai))

  return rows
}

function row(id: StatusRowId, name: string, state: State): StatusRow {
  return { id, name, state: state.label.toLowerCase(), tone: state.tone }
}

function aiState(health: SyncHealth): State | null {
  const spend = health.aiSpend ?? []
  const calls = health.aiCalls ?? null

  if (spend.length === 0 && !calls) return null

  if (spend.some((w) => w.reached)) return { label: 'limit reached', tone: 'bad' }
  if (calls && calls.errors + calls.timedOut > 0) return { label: 'calls failing', tone: 'bad' }
  if (spend.length > 0) return { label: 'close to a limit', tone: 'warn' }
  if (calls && calls.fallbacks > 0) return { label: 'on the fallback', tone: 'warn' }

  return { label: 'working', tone: 'ok' }
}
