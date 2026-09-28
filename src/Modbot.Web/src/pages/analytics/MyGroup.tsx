import { useEffect, useState } from 'react'
import { AlertsCard } from '@/components/alerts/AlertsCard'
import { compactNumber, dateTime } from '@/components/charts'
import { api, type CurrentUser, type GroupAnalytics } from '@/lib/api'
import { weekPair } from '@/lib/groupOverview'
import { mayOpen, type PageId } from '@/lib/nav'
import { GroupOverview } from './GroupOverview'
import { Stat } from './shared'
import { WeekStat, WeekStrip } from './WeekStrip'

/**
 * The VRChat page's Overview, laid out the way the group's own VRChat page is (`GroupOverview`):
 * the banner, name and counts, VRChat's row of tabs, and the overview cards. Under them, this week's
 * four numbers, the same shape as the Discord page's. The charts that used to follow are on the
 * Stats page, split by question (Stats page design); the week's heading opens them.
 */
export function MyGroup({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  return (
    <div className="flex flex-col gap-3">
      <GroupOverview me={me} pathOf={pathOf} />
      <AlertsCard />
      <GroupWeek me={me} pathOf={pathOf} />
    </div>
  )
}

/**
 * New members, leavers, join requests and the most online at once over the last seven days, each
 * but the last against the seven before. The week ends today, not over yet, as the Discord page's
 * does (`ServerWeek`).
 *
 * Read from the group's analytics twice, for fourteen days (both weeks' daily totals) and for seven
 * (the week's own peak), with no read of VRChat. The most online is one moment, so it has no fair
 * figure from last week to set beside it and shows when it happened instead.
 *
 * Nothing is drawn while it loads or if it cannot be read: the overview above is the page, and the
 * week is not worth an error in front of it.
 */
function GroupWeek({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [weeks, setWeeks] = useState<{ fortnight: GroupAnalytics; week: GroupAnalytics } | null>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([api.groupAnalytics('days=14'), api.groupAnalytics('days=7')])
      .then(([fortnight, week]) => {
        if (!cancelled) setWeeks({ fortnight, week })
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [])

  if (!weeks) return null

  const { fortnight, week } = weeks
  const online = week.peaks.online

  return (
    <WeekStrip href={mayOpen(me, 'stats') ? pathOf('stats') : undefined}>
      <WeekStat label="New members" pair={weekPair(fortnight.joined, fortnight.to)} />
      <WeekStat label="Left" pair={weekPair(fortnight.left, fortnight.to)} upIsGood={false} />
      <WeekStat label="Join requests" pair={weekPair(fortnight.requestsReceived, fortnight.to)} />
      <Stat
        label="Most online at once"
        value={online ? compactNumber(online.value) : '—'}
        note={online ? dateTime(online.at) : undefined}
        noteMono
      />
    </WeekStrip>
  )
}
