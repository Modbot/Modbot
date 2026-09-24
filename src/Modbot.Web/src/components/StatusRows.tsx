import { useEffect, useState } from 'react'
import { api, type SyncHealth } from '@/lib/api'
import { DOT, statusRows, TONE, type StatusRowId } from '@/lib/status'
import { useGateHealth } from '@/lib/useGateHealth'
import { cn } from '@/lib/utils'

/**
 * The rows at the foot of the sidebar: VRChat, Discord, Database, Sync, and AI once it is in use.
 *
 * This is the one place an operator looks to find out that something is wrong, so a part that has
 * not answered reads "unknown" rather than green. Clicking a row opens the Health page at that
 * part's card.
 *
 * It replaced the single VRChat dot and the **Sync health** entry in the page list above: the
 * rows say the same thing in more detail and lead to the same page.
 */

/** Slower than the Health page's own poll: this sits on every screen, and the sync read is not cheap. */
const EVERY_MS = 30_000

export function StatusRows({ onOpen }: { onOpen: (section: StatusRowId) => void }) {
  // Shared with the sign-in wait banner, so the two read one poll rather than two.
  const { gate, failed } = useGateHealth()
  const [health, setHealth] = useState<SyncHealth | null>(null)
  const [databaseReachable, setDatabaseReachable] = useState<boolean | null>(null)

  useEffect(() => {
    let cancelled = false

    const load = () => {
      api
        .syncHealth()
        .then((next) => !cancelled && setHealth(next))
        .catch(() => !cancelled && setHealth(null))

      api
        .databaseHealth()
        .then((ok) => !cancelled && setDatabaseReachable(ok))
        .catch(() => !cancelled && setDatabaseReachable(null))
    }

    load()
    const timer = setInterval(load, EVERY_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  const rows = statusRows({
    gate: failed ? null : (gate?.status ?? null),
    health,
    databaseReachable,
  })

  return (
    <div className="mt-auto flex flex-col border-t border-t-(length:--hairline) pt-2">
      {rows.map((r) => (
        <button
          key={r.id}
          type="button"
          onClick={() => onOpen(r.id)}
          className="flex w-full items-center gap-2 px-4 py-1 text-left hover:bg-card/60"
          style={{ fontSize: 'var(--text-small)' }}
        >
          <span aria-hidden className={cn('size-1.5 shrink-0', DOT[r.tone])} />
          <span className="truncate text-muted-foreground">{r.name}</span>
          <span className={cn('ml-auto truncate', r.tone === 'ok' ? 'text-muted-foreground' : TONE[r.tone])}>
            {r.state}
          </span>
        </button>
      ))}
    </div>
  )
}
