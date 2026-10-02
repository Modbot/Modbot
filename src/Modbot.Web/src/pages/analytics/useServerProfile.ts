import { useEffect, useState } from 'react'
import { api, type CurrentUser, type ServerProfile } from '@/lib/api'
import { can } from '@/lib/permissions'
import { rangeQuery } from './useAnalytics'

/**
 * The server for the Discord page's header, over a tab that needs only See analytics (Roles,
 * Channels). Read on its own, so the tab below never waits on it, and left null when it cannot be
 * read, so the header is simply not drawn.
 *
 * The header's own read is gated on See members, like the member list it also sits over. Somebody
 * who may see analytics but not members gets the same server from the Discord page's analytics
 * answer instead, the way the Overview tab does.
 */
export function useServerProfile(me: CurrentUser): ServerProfile | null {
  const [server, setServer] = useState<ServerProfile | null>(null)
  const members = can(me, 'ViewMembers')
  const analytics = can(me, 'ViewAnalytics')

  useEffect(() => {
    if (!members && !analytics) return
    let cancelled = false

    const read = members
      ? api.discordServer()
      : api.serverAnalytics(rangeQuery(7)).then((answer) => answer.server)

    read
      .then((next) => {
        if (!cancelled) setServer(next)
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [members, analytics])

  return server
}
