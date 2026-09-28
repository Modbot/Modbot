import { useCallback, useState } from 'react'
import { Tabs } from '@/components/ui/tabs'
import { api, type CurrentUser } from '@/lib/api'
import type { PageId } from '@/lib/nav'
import { GroupGrowth, MostOnline } from './GroupStats'
import { InsightsPanel } from './InsightsPanel'
import { InstanceStats } from './InstanceStats'
import { TeamStats } from './MyTeam'
import { ServerActivity, ServerCoverage, ServerGrowth, ServerModeration } from './ServerStats'
import { CoverageLine, PageMessage, RangePicker, Section } from './shared'
import { useAnalytics, type Range } from './useAnalytics'
import { WorldStats } from './Worlds'

/** The Stats page's tabs, one per question, each at an address of its own. */
export type StatsTab = 'stats' | 'stats-activity' | 'stats-moderation'

const TABS: { value: StatsTab; label: string }[] = [
  { value: 'stats', label: 'Growth' },
  { value: 'stats-activity', label: 'Activity' },
  { value: 'stats-moderation', label: 'Moderation' },
]

/**
 * Stats: the charts and numbers over time for every platform on one page, a tab per question
 * (Stats page design, spec 10.1). Growth is whether the community is growing, Activity is when it
 * is busy and where, Moderation is who is doing the work and when nobody is covering.
 *
 * Inside a tab each platform has a part of its own under its name, and no chart or number adds
 * VRChat and Discord together: a VRChat join and a Discord join are not the same kind of thing, and
 * one line through both would say they were.
 *
 * One range for the whole page, kept while the tabs change, so switching from Growth to Activity
 * compares the same days. Each part reads its own analytics when its tab is first opened and again
 * only when the range changes; the Discord server's answer is shared by all three tabs.
 */
export function Stats({
  me,
  pathOf,
  tab,
  onTab,
  onOpenSubject,
  onOpenReviews,
}: {
  me: CurrentUser
  pathOf: (id: PageId) => string
  tab: StatsTab
  onTab: (tab: StatsTab) => void
  onOpenSubject: (subjectId: string) => void
  /** Opens the Reviews page. Passed only when the signed-in person may review. */
  onOpenReviews?: () => void
}) {
  const [range, setRange] = useState<Range>(30)

  const loadGroup = useCallback((q: string) => api.groupAnalytics(q), [])
  const loadServer = useCallback((q: string) => api.serverAnalytics(q), [])
  const loadInstances = useCallback((q: string) => api.instancesAnalytics(q), [])
  const loadWorlds = useCallback((q: string) => api.worldsAnalytics(q), [])
  const loadTeam = useCallback((q: string) => api.teamAnalytics(q), [])

  const group = useAnalytics(loadGroup, range, tab !== 'stats-moderation')
  const server = useAnalytics(loadServer, range)
  const instances = useAnalytics(loadInstances, range, tab === 'stats-activity')
  const worlds = useAnalytics(loadWorlds, range, tab === 'stats-activity')
  const team = useAnalytics(loadTeam, range, tab === 'stats-moderation')

  // The days the picker names: the first VRChat part's, which every part of the tab shares.
  const shown = tab === 'stats-moderation' ? team.data : tab === 'stats-activity' ? instances.data : group.data
  const links = { me, pathOf }

  return (
    <Tabs value={tab} onChange={onTab} tabs={TABS} className="gap-3">
      <div className="flex flex-col gap-4">
        <RangePicker range={range} onChange={setRange} from={shown?.from} to={shown?.to} />

        {shown && <CoverageLine coverage={shown.coverage} generatedAt={shown.generatedAt} />}

        {tab === 'stats' && (
          <>
            <InsightsPanel only="group" />
            <Part title="VRChat group" read={group}>
              {(data) => <GroupGrowth data={data} />}
            </Part>
            <Part title="Discord" read={server} coverage={(data) => <ServerCoverage data={data} {...links} />}>
              {(data) => <ServerGrowth data={data} />}
            </Part>
          </>
        )}

        {tab === 'stats-activity' && (
          <>
            <InsightsPanel only="instances" />
            <Part title="VRChat instances" read={instances}>
              {(data) => (
                <InstanceStats data={data} mostOnline={group.data && <MostOnline peaks={group.data.peaks} />} />
              )}
            </Part>
            <Part title="VRChat worlds" read={worlds}>
              {(data) => <WorldStats data={data} />}
            </Part>
            <Part title="Discord" read={server} coverage={(data) => <ServerCoverage data={data} {...links} />}>
              {(data) => <ServerActivity data={data} />}
            </Part>
          </>
        )}

        {tab === 'stats-moderation' && (
          <>
            <InsightsPanel only="team" />
            <Part title="Team" read={team}>
              {(data) => <TeamStats data={data} onOpenSubject={onOpenSubject} onOpenReviews={onOpenReviews} />}
            </Part>
            <Part title="Discord" read={server} coverage={(data) => <ServerCoverage data={data} {...links} />}>
              {(data) => <ServerModeration data={data} {...links} />}
            </Part>
          </>
        )}
      </div>
    </Tabs>
  )
}

/**
 * One platform's part of a tab, under its name: loading, failed, or drawn from what was read. A part
 * that fails says so in its own place and leaves the other parts of the tab standing. `coverage`
 * draws a part's own coverage line, for the Discord server, whose reach is not the group's.
 */
function Part<T>({
  title,
  read,
  coverage,
  children,
}: {
  title: string
  read: { data: T | null; error: string | null }
  coverage?: (data: T) => React.ReactNode
  children: (data: T) => React.ReactNode
}) {
  return (
    <Section title={title}>
      {read.error ? (
        <PageMessage tone="danger">{read.error}</PageMessage>
      ) : !read.data ? (
        <PageMessage>Loading…</PageMessage>
      ) : (
        <>
          {coverage?.(read.data)}
          {children(read.data)}
        </>
      )}
    </Section>
  )
}
