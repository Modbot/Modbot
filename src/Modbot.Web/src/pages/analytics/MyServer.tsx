import { useCallback, useEffect, useState } from 'react'
import { minutes } from '@/components/charts'
import { api, type CurrentUser, type ServerProfile } from '@/lib/api'
import { mayOpen, type PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { seesVoice } from '@/lib/serverOverview'
import { PageMessage } from './shared'
import { ServerHeader } from './ServerHeader'
import { ServerVoice } from './ServerVoice'
import { useAnalytics } from './useAnalytics'
import { WeekStat, WeekStrip } from './WeekStrip'

/**
 * The Discord page: the server as Discord shows it, who is in voice, and this week's four numbers.
 *
 * The server's header comes first, as its server profile has it, read on its own so it is there
 * before anything else. Under it, who is in voice now, as Discord's channel list shows it, for
 * someone who may open Live. Then "This week": the four numbers Discord's Server Insights opens
 * on, each against the week before. The charts that used to follow are on the Stats page, split by
 * question (Stats page design); the heading opens them.
 *
 * The week is the last seven days whatever range is asked for, so the page asks for the shortest.
 */
export function MyServer({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const load = useCallback((q: string) => api.serverAnalytics(q), [])
  const { data, error } = useAnalytics(load, 7)
  const header = useServerHeader(me)

  if (error) return <PageMessage tone="danger">{error}</PageMessage>

  const server = header ?? data?.server

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} />}

      {seesVoice(me) && <ServerVoice />}

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <WeekStrip href={mayOpen(me, 'stats-activity') ? pathOf('stats-activity') : undefined}>
          <WeekStat label="New members" pair={data.week.newMembers} />
          <WeekStat label="Talked" pair={data.week.talked} />
          <WeekStat label="Messages" pair={data.week.messages} />
          <WeekStat label="Time in voice" pair={data.week.voiceMinutes} format={minutes} />
        </WeekStrip>
      )}
    </div>
  )
}

/**
 * The header's server, read on its own so the header is drawn before the week arrives. That read
 * is gated on See members, like the member list it also sits over; someone who may see analytics
 * but not members gets the header from the analytics answer instead, once it is there.
 */
function useServerHeader(me: CurrentUser): ServerProfile | null {
  const [server, setServer] = useState<ServerProfile | null>(null)
  const allowed = can(me, 'ViewMembers')

  useEffect(() => {
    if (!allowed) return
    let cancelled = false

    api
      .discordServer()
      .then((next) => {
        if (!cancelled) setServer(next)
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [allowed])

  return server
}
