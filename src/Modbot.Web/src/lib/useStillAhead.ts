import { useEffect, useState } from 'react'

/**
 * A time an outside service holds Modbot until (a limit, or a wait before signing in again), while it
 * is still ahead; null once it has passed, so whatever the time hid comes back by itself. The server
 * sends the time only while it is ahead.
 */
export function useStillAhead(until: string | null): string | null {
  const [passed, setPassed] = useState<string | null>(null)

  useEffect(() => {
    if (!until) return
    const timer = window.setTimeout(() => setPassed(until), Math.max(0, new Date(until).getTime() - Date.now()))
    return () => window.clearTimeout(timer)
  }, [until])

  return until && passed !== until ? until : null
}
