import { useSyncExternalStore } from 'react'
import { outbox } from './outbox.ts'
import type { OutboxEntry } from './storage.ts'

/** The addresses this browser has not yet managed to send to the server. */
export function usePendingSends(): readonly OutboxEntry[] {
  return useSyncExternalStore(outbox.subscribe, outbox.entries, outbox.entries)
}
