import { AlertTriangle, CheckCircle2, CircleSlash, PauseCircle } from 'lucide-react'
import type { GateStatus } from '@/lib/api'
import { vrchatState, type State } from '@/lib/status'

/**
 * The gate's status with an icon on it, for the screens that draw one.
 *
 * The words and the colour live in `lib/status.ts`, which every part of Modbot's health reads;
 * this adds only the picture. Splitting them keeps the words loadable without a rendering
 * library, so the tests can read the same table the screens do.
 */
const ICON: Record<GateStatus, typeof CheckCircle2> = {
  Working: CheckCircle2,
  WaitingOnPurpose: PauseCircle,
  NeedsOperator: AlertTriangle,
  NotConfigured: CircleSlash,
}

export function statusOf(status: string | null | undefined): State & { icon: typeof CheckCircle2 } {
  return { ...vrchatState(status), icon: ICON[status as GateStatus] ?? CircleSlash }
}
