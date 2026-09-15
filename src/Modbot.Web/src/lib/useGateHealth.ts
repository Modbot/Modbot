import { useSyncExternalStore } from 'react'
import { api, type GateHealth } from '@/lib/api'

/**
 * One poll of `/api/health/gate` for the whole page, however many components read it.
 *
 * The sidebar dot and the sign-in wait banner both need it, and two timers would ask the server
 * twice as often for the same answer. While Modbot is waiting to sign in the poll speeds up, so the
 * banner goes away soon after signing in works again.
 */

export type GateHealthSnapshot = {
  gate: GateHealth | null
  failed: boolean
  /** `performance.now()` when `gate` arrived, so a countdown can subtract time since without trusting the browser's clock. */
  receivedAt: number
}

const NORMAL_MS = 30_000
const WAITING_MS = 15_000

let snapshot: GateHealthSnapshot = { gate: null, failed: false, receivedAt: 0 }
const listeners = new Set<() => void>()
let timer: ReturnType<typeof setTimeout> | null = null
let inFlight: Promise<void> | null = null

function publish(next: GateHealthSnapshot) {
  snapshot = next
  for (const listener of listeners) listener()
}

function schedule() {
  if (timer) clearTimeout(timer)
  if (listeners.size === 0) {
    timer = null
    return
  }

  timer = setTimeout(() => void refreshGateHealth(), snapshot.gate?.signInWait ? WAITING_MS : NORMAL_MS)
}

/** Asks now rather than at the next tick -- after a sign-in was refused, say. */
export function refreshGateHealth(): Promise<void> {
  if (inFlight) return inFlight

  inFlight = api
    .gateHealth()
    .then((gate) => publish({ gate, failed: false, receivedAt: performance.now() }))
    .catch(() => publish({ ...snapshot, failed: true }))
    .finally(() => {
      inFlight = null
      schedule()
    })

  return inFlight
}

function subscribe(listener: () => void) {
  listeners.add(listener)

  if (listeners.size === 1) void refreshGateHealth()

  return () => {
    listeners.delete(listener)
    if (listeners.size === 0 && timer) {
      clearTimeout(timer)
      timer = null
    }
  }
}

export function useGateHealth(): GateHealthSnapshot {
  return useSyncExternalStore(subscribe, () => snapshot)
}
