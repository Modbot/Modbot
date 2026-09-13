import { AlertTriangle, CheckCircle2, CircleSlash, PauseCircle } from 'lucide-react'
import type { GatePosture } from '@/lib/api'

export type Tone = 'ok' | 'warn' | 'bad' | 'muted'

/**
 * How each gate posture is presented — one table, read by both the sidebar dot and the health
 * screen.
 *
 * The two must never disagree about whether Modbot is broken, and the surest way to guarantee
 * that is for both to read the same table, with the posture itself decided by the server rather
 * than by either of them.
 */
export const POSTURE: Record<
  GatePosture,
  { label: string; tone: Tone; icon: typeof CheckCircle2 }
> = {
  Working: { label: 'Working', tone: 'ok', icon: CheckCircle2 },

  // Deliberately not an error tone. Waiting out a rate limit is spec 4.3.1 behaving correctly and
  // recovering on its own; painting it red trains an operator to intervene, and against a penalty
  // that grows on every probe, intervening is the one thing that makes it worse.
  WaitingOnPurpose: { label: 'Waiting on purpose', tone: 'warn', icon: PauseCircle },

  // Broken. Identical to the line above from outside -- traffic stopped, data not arriving -- and
  // the opposite meaning: nothing changes until somebody does something.
  NeedsOperator: { label: 'Needs you', tone: 'bad', icon: AlertTriangle },

  NotConfigured: { label: 'Not configured', tone: 'muted', icon: CircleSlash },
}

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
