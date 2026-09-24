import { useEffect, useState } from 'react'
import { api, type ModbotNotification } from '@/lib/api'

/**
 * Critical notifications that reached this person on no channel at all (foundation §4.5.3).
 *
 * The last sentence of §4.5.3 is the reason this exists: a critical alert nobody can receive is the
 * same as no alerting at all, so the one place it is certain to be seen is the moment they sign in.
 * Read once per page load, above everything, and it goes when they say they have seen it.
 */
export function WaitingAlertsBanner() {
  const [waiting, setWaiting] = useState<ModbotNotification[]>([])

  useEffect(() => {
    let live = true

    api
      .notifications()
      .then((r) => live && setWaiting(r.waiting))
      .catch(() => {})

    return () => {
      live = false
    }
  }, [])

  if (waiting.length === 0) return null

  const seen = async (id: string) => {
    setWaiting((all) => all.filter((n) => n.id !== id))

    try {
      await api.markNotificationSeen(id)
    } catch {
      // Nothing to do here. The row is still marked waiting on the server, so it comes back on the
      // next page load rather than being quietly lost, which is the whole point of the rule.
    }
  }

  return (
    <div
      role="alert"
      className="w-full border-b border-b-(length:--hairline) border-destructive/40 bg-destructive/10 px-4 py-1 text-destructive lg:px-5"
    >
      {waiting.map((n) => (
        <div key={n.id} className="flex min-h-(--strip-h) flex-wrap items-center gap-x-2 gap-y-1" style={{ fontSize: 'var(--text-small)' }}>
          <span aria-hidden className="size-2 shrink-0 bg-destructive" />
          <span className="font-medium">{n.title}</span>
          <span>{n.body}</span>
          <button type="button" className="underline" onClick={() => seen(n.id)}>
            Seen
          </button>
        </div>
      ))}
    </div>
  )
}
