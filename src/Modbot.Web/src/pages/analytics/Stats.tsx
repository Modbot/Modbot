import { useCallback, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Tabs } from '@/components/ui/tabs'
import { api, type CurrentUser } from '@/lib/api'
import type { PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { GroupGrowth, MostOnline } from './GroupStats'
import { InsightsPanel } from './InsightsPanel'
import { HEATMAP_ID, InstanceStats } from './InstanceStats'
import { TeamStats } from './MyTeam'
import { sectionId } from './sectionId'
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

// The Activity tab's parts, named once for their headings and for the jumps to them.
const INSTANCES = 'VRChat instances'
const WORLDS = 'VRChat worlds'
const DISCORD = 'Discord'

const JUMPS: { label: string; id: string }[] = [
  { label: 'Instances', id: sectionId(INSTANCES) },
  { label: 'Heatmap', id: HEATMAP_ID },
  { label: 'Worlds', id: sectionId(WORLDS) },
  { label: 'Discord', id: sectionId(DISCORD) },
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
  // The Moderation tab's people bar. Until the reader picks one this session the tab reads the
  // group's saved bar, so a change another administrator saved shows on the next read. A reader who
  // may change settings saves their pick and reads the saved bar again; anybody else's pick is their
  // own view only, sent with the read (`people`).
  const [people, setPeople] = useState<number | null>(null)
  const canSavePeople = can(me, 'ManageSettings')
  const loadTeam = useCallback(
    (q: string) => api.teamAnalytics(people === null ? q : `${q}&people=${people}`),
    [people],
  )
  // The pick that failed to save, so "Try again" saves that same pick again.
  const [peopleError, setPeopleError] = useState<{ message: string; next: number } | null>(null)
  // A pick being saved, shown on the buttons until the read after the save replaces `over`.
  const [saving, setSaving] = useState<{ people: number; over: unknown } | null>(null)

  const group = useAnalytics(loadGroup, range, tab !== 'stats-moderation')
  const server = useAnalytics(loadServer, range)
  const instances = useAnalytics(loadInstances, range, tab === 'stats-activity')
  const worlds = useAnalytics(loadWorlds, range, tab === 'stats-activity')
  const team = useAnalytics(loadTeam, range, tab === 'stats-moderation')

  const { data: teamData, reload: reloadTeam } = team
  const choosePeople = useCallback(
    (next: number) => {
      setPeopleError(null)
      if (!canSavePeople) {
        setPeople(next)
        return
      }
      setSaving({ people: next, over: teamData })
      api
        .setCoverPeople(next)
        .then(() => reloadTeam?.())
        .catch(() => {
          setSaving(null)
          setPeopleError({ message: `Could not save ${next}+ people as the group's setting.`, next })
        })
    },
    [canSavePeople, teamData, reloadTeam],
  )
  const picked = saving && saving.over === teamData ? saving.people : (people ?? undefined)

  // The days the picker names: the first VRChat part's, which every part of the tab shares.
  const shown = tab === 'stats-moderation' ? team.data : tab === 'stats-activity' ? instances.data : group.data
  const links = { me, pathOf }

  return (
    <Tabs value={tab} onChange={onTab} tabs={TABS} className="gap-3">
      <div className="flex flex-col gap-4">
        <RangePicker range={range} onChange={setRange} from={shown?.from} to={shown?.to} />

        {tab === 'stats-activity' && <Jumps />}

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
            <Part title={INSTANCES} read={instances}>
              {(data) => (
                <InstanceStats data={data} mostOnline={group.data && <MostOnline peaks={group.data.peaks} />} />
              )}
            </Part>
            <Part title={WORLDS} read={worlds}>
              {(data) => <WorldStats data={data} />}
            </Part>
            <Part title={DISCORD} read={server} coverage={(data) => <ServerCoverage data={data} {...links} />}>
              {(data) => <ServerActivity data={data} />}
            </Part>
          </>
        )}

        {tab === 'stats-moderation' && (
          <>
            <InsightsPanel only="team" />
            <Part title="Team" read={team}>
              {(data) => (
                <>
                  {peopleError && (
                    <PageMessage tone="danger" onTryAgain={() => choosePeople(peopleError.next)}>
                      {peopleError.message}
                    </PageMessage>
                  )}
                  <TeamStats
                    data={data}
                    onOpenSubject={onOpenSubject}
                    onOpenReviews={onOpenReviews}
                    picked={picked}
                    onPeople={choosePeople}
                  />
                </>
              )}
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
 * A row of jumps to the Activity tab's parts, for a screen where the tab runs to several screens.
 * Below the sidebar's breakpoint only: a desktop sees most of a part at once and has no need. Each
 * jump scrolls its target under the sticky top bar, which the target's own scroll margin clears.
 */
function Jumps() {
  return (
    <nav aria-label="Parts of this tab" className="-my-2 -ml-2.5 flex flex-wrap items-center lg:hidden">
      {JUMPS.map((jump, i) => (
        <span key={jump.id} className="flex items-center">
          {i > 0 && (
            <span aria-hidden className="text-muted-foreground">
              ·
            </span>
          )}
          <Button
            variant="ghost"
            size="sm"
            onClick={() => document.getElementById(jump.id)?.scrollIntoView({ block: 'start' })}
          >
            {jump.label}
          </Button>
        </span>
      ))}
    </nav>
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
  read: { data: T | null; error: string | null; reload?: (() => void) | null }
  coverage?: (data: T) => React.ReactNode
  children: (data: T) => React.ReactNode
}) {
  return (
    <Section title={title}>
      {read.error ? (
        <PageMessage tone="danger" onTryAgain={read.reload}>{read.error}</PageMessage>
      ) : !read.data ? (
        <PageMessage tone="loading" />
      ) : (
        <>
          {coverage?.(read.data)}
          {children(read.data)}
        </>
      )}
    </Section>
  )
}
