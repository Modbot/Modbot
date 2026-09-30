import { useState } from 'react'
import { User } from 'lucide-react'
import { DailyBars, DailyLine, Heatmap, compactNumber, dateTime, longDay, minutes, percent } from '@/components/charts'
import { HeadCount } from '@/components/HeadCount'
import type { HourOfWeek, InstancePeaks, InstancesAnalytics } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { InstanceActivityChart } from './InstanceActivityChart'
import { PageMessage, Panel, Stat, StatStrip, Toggle } from './shared'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const HOURS = Array.from({ length: 24 }, (_, h) => `${h}:00`)

/** The heatmap's anchor, for the Activity tab's jumps. */
export const HEATMAP_ID = 'heatmap'

/** Below this many presence reports in the range, the presence panels are shown but called thin. */
const THIN_REPORTS = 200

/**
 * The group's instances on the Stats page's Activity tab: when is the community actually active?
 * (spec 10.1, Stats page design). They were the charts under the VRChat page's Instances tab, which
 * keeps what vrchat.com shows there: what is open now and what ran lately.
 *
 * Opened and manually closed per day come from the daily totals; "manually closed" is VRChat's close
 * entries, which it writes only for a close by hand. Naturally ended, how many are open at once,
 * how long instances stay open and the heatmap's openings come from the instance table, which
 * records every end however it came about. The heatmap is the cyclic answer
 * spec 5.10 says a daily series cannot give: "Tuesdays at 8pm are our busiest hour" is only visible
 * once the days are laid over each other.
 *
 * The peaks and the activity line come from a third source, VRChat's own head counts, which need no
 * moderator's companion and so cover instances presence reports cannot see. Their coverage is its
 * own figure and is marked when it is thin; the presence panels carry the report count for the same
 * reason, the same way the worlds part does.
 */
export function InstanceStats({
  data,
  mostOnline,
}: {
  data: InstancesAnalytics
  /** The group's most online at once, first in the strip: it is read with the group's analytics. */
  mostOnline?: React.ReactNode
}) {
  const [layer, setLayer] = useState<'arrivals' | 'opened'>('arrivals')

  const sum = (points: { value: number }[]) => points.reduce((s, p) => s + p.value, 0)
  const max = (points: { value: number }[]) => points.reduce((m, p) => Math.max(m, p.value), 0)

  return (
    <PanelGrid className="grid-cols-1">
      {/* Five tiles, or six with the group's most online first: six is two even rows of three on a phone, five goes in pairs. */}
      <StatStrip className={mostOnline ? 'md:grid-cols-3 xl:grid-cols-6' : 'md:grid-cols-3 xl:grid-cols-5'} phonePairs={!mostOnline}>
        {mostOnline}
        <Stat label="Opened" value={compactNumber(sum(data.opened))} />
        <Stat label="Manually closed" value={compactNumber(sum(data.closedByHand))} />
        <Stat label="Naturally ended" value={compactNumber(sum(data.endedOnTheirOwn))} />
        <Stat label="Typical time open" value={data.typicalMinutesOpen === null ? '—' : minutes(data.typicalMinutesOpen)} />
        <Stat label="Most open at once" value={compactNumber(max(data.mostOpenAtOnce))} />
      </StatStrip>

      <Peaks peaks={data.peaks} />

      <InstanceActivityChart />

      {data.presenceReports > 0 && data.presenceReports < THIN_REPORTS && (
        <PageMessage>
          Only <span className="font-mono">{compactNumber(data.presenceReports)}</span> presence reports in this range.
        </PageMessage>
      )}

      <Panel
        id={HEATMAP_ID}
        title="When the community is active (your time)"
        flush={sum(toPoints(data.hourOfWeek[layer])) === 0}
        right={
          <Toggle
            value={layer}
            onChange={setLayer}
            options={[
              { value: 'arrivals', label: 'People arriving' },
              { value: 'opened', label: 'Instances opened' },
            ]}
          />
        }
      >
        {sum(toPoints(data.hourOfWeek[layer])) === 0 ? (
          <EmptyRow>{layer === 'arrivals' ? 'No arrivals seen in this range.' : 'No instances opened in this range.'}</EmptyRow>
        ) : (
          <Heatmap
            rows={DAYS}
            cols={HOURS}
            values={toLocalGrid(data.hourOfWeek[layer])}
            valueLabel={layer === 'arrivals' ? 'arrivals' : 'instances opened'}
            valueLabelOne={layer === 'arrivals' ? 'arrival' : 'instance opened'}
            slot={layer === 'arrivals' ? 1 : 4}
          />
        )}
      </Panel>

      <PanelGrid className="lg:grid-cols-2">
        <Panel title="Opened, manually closed and naturally ended per day">
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutAuditLog}
            today={data.today}
            legend={[
              { label: 'Opened', slot: 1 },
              { label: 'Manually closed', slot: 2 },
              { label: 'Naturally ended', slot: 3 },
            ]}
            series={[
              { key: 'opened', label: 'opened', points: data.opened, slot: 1 },
              { key: 'closed', label: 'manually closed', points: data.closedByHand, slot: 2 },
              { key: 'ended', label: 'naturally ended', points: data.endedOnTheirOwn, slot: 3 },
            ]}
          />
        </Panel>

        <Panel title="Most open at once, per day">
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysBeforeModbot}
            today={data.today}
            series={[{ key: 'open', label: 'open at once', points: data.mostOpenAtOnce, slot: 4 }]}
          />
        </Panel>
      </PanelGrid>

      <PanelGrid className="lg:grid-cols-2">
        <Panel title="Most people at once, per day">
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutHeadCounts}
            today={data.today}
            series={[{ key: 'people', label: 'people', one: 'person', points: data.peaks.mostPeopleAtOncePerDay, slot: 3 }]}
          />
        </Panel>

        {/*
          People-time, so a long steady evening outweighs a short rush. Hours rather than
          minutes on the axis: a busy day is thousands of people-minutes and a chart of
          thousands is a chart nobody reads.
        */}
        <Panel title="People-hours per day">
          <DailyLine
            from={data.from}
            to={data.to}
            missing={data.daysWithoutHeadCounts}
            today={data.today}
            series={[{ key: 'busy', label: 'people-hours', one: 'person-hour', points: toHours(data.peaks.peopleMinutesPerDay), slot: 1 }]}
            format={(v) => `${compactNumber(v)}h`}
          />
        </Panel>
      </PanelGrid>

      <PanelGrid className="lg:grid-cols-2">
        {/* A gap, not nought, on a day nothing ended: that day has no typical length. */}
        <Panel title="Typical time open, per day">
          <DailyLine
            from={data.from}
            to={data.to}
            mode="gap"
            missing={data.daysBeforeModbot}
            today={data.today}
            series={[{ key: 'open', label: 'typical time open', points: data.typicalMinutesOpenPerDay, slot: 4 }]}
            format={minutes}
          />
        </Panel>

        <Panel title="Most people seen in one instance, per day" flush={data.presenceReports === 0}>
          {data.presenceReports === 0 ? (
            <EmptyRow>No presence reports in this range.</EmptyRow>
          ) : (
            <DailyBars
              from={data.from}
              to={data.to}
              missing={data.daysWithoutPresenceReports}
              today={data.today}
              series={[{ key: 'people', label: 'people', one: 'person', points: data.mostPeopleInOne, slot: 2 }]}
            />
          )}
        </Panel>
      </PanelGrid>
    </PanelGrid>
  )
}

const toPoints = (buckets: number[]) => buckets.map((value) => ({ value }))

const toHours = (points: { day: string; value: number }[]) =>
  points.map((p) => ({ day: p.day, value: Math.round((p.value / 60) * 10) / 10 }))

/**
 * How full it ever got, and when.
 *
 * Every figure here is from VRChat's own head counts, so it covers instances no moderator's
 * companion was in. A peak carries its moment because a peak without one cannot be rostered
 * against, and the coverage line above it says how much of the time instances were open Modbot
 * actually had a count for -- without which "48 people" could as easily be the week's high as the
 * highest of the two hours anybody was counting.
 */
function Peaks({ peaks }: { peaks: InstancePeaks }) {
  const { coverage } = peaks
  const busiestDayPeak = peaks.busiestDay
    ? (peaks.mostPeopleAtOncePerDay.find((p) => p.day === peaks.busiestDay?.day)?.value ?? null)
    : null

  return (
    <>
      {coverage.instancesOpen > 0 && coverage.instancesCounted === 0 ? (
        <PageMessage>No head counts in this range.</PageMessage>
      ) : coverage.thin ? (
        <PageMessage>
          Head counts cover{' '}
          <span className="font-mono">{percent(coverage.minutesCounted, coverage.minutesInstancesWereOpen)}</span> of the time
          instances were open.
        </PageMessage>
      ) : null}

      <StatStrip phonePairs>
        <Stat
          label="Most people at once"
          value={
            peaks.mostPeopleAtOnce ? (
              <HeadCount count={peaks.mostPeopleAtOnce.value} unsure={peaks.mostPeopleAtOnce.unsure} format={compactNumber} />
            ) : (
              '—'
            )
          }
          note={peaks.mostPeopleAtOnce ? dateTime(peaks.mostPeopleAtOnce.at) : undefined}
          noteMono
        />
        <Stat
          label="Fullest instance"
          value={
            peaks.busiestInstance ? (
              <HeadCount count={peaks.busiestInstance.people} unsure={peaks.busiestInstance.unsure} format={compactNumber} />
            ) : (
              '—'
            )
          }
          note={
            peaks.busiestInstance ? (
              <>
                {peaks.busiestInstance.worldName ?? <span className="font-mono">{peaks.busiestInstance.worldId}</span>} ·{' '}
                <span className="font-mono whitespace-nowrap">{dateTime(peaks.busiestInstance.at)}</span>
              </>
            ) : undefined
          }
        />
        {/*
          Both are picked by people-time -- everyone's minutes in the group's instances added up --
          and neither shows it. Printed as a length of time, "4h 22m" read as how long
          something lasted, and divided back into people it gave 1.4 of a person. The hour shows
          its average in whole people, the exact figure on hover; the day shows the most at once
          that day, because a day's average is always nearly nobody.
        */}
        <Stat
          label="Busiest day"
          value={busiestDayPeak === null ? '—' : <People count={busiestDayPeak} unsure={peaks.busiestDay?.unsure} />}
          note={peaks.busiestDay ? `${longDay(peaks.busiestDay.day)} UTC` : undefined}
          noteMono
        />
        <Stat
          label="Busiest hour"
          value={
            peaks.busiestHour ? (
              <People
                count={Math.round(peaks.busiestHour.peopleMinutes / 60)}
                unsure={peaks.busiestHour.unsure}
                title={`${(peaks.busiestHour.peopleMinutes / 60).toFixed(1)} on average`}
              />
            ) : (
              '—'
            )
          }
          note={peaks.busiestHour ? dateTime(peaks.busiestHour.startedAt) : undefined}
          noteMono
        />
      </StatStrip>
    </>
  )
}

/**
 * Shifts the 168 UTC buckets into the viewer's clock and lays them out by day.
 *
 * Whole hours only. A half-hour zone lands half an hour off; the
 * alternative -- buckets by the viewer's zone on the server -- would make the same query return
 * different rows to two moderators in different countries, and the cache would lie to one of them.
 */
function toLocalGrid(buckets: HourOfWeek['arrivals']): number[][] {
  const shift = Math.round(-new Date().getTimezoneOffset() / 60)
  const grid = DAYS.map(() => new Array<number>(24).fill(0))

  buckets.forEach((value, utcIndex) => {
    const local = (((utcIndex + shift) % 168) + 168) % 168
    grid[Math.floor(local / 24)][local % 24] += value
  })

  return grid
}

/** A number of people, drawn with a person beside it. */
function People({ count, unsure = false, title }: { count: number; unsure?: boolean; title?: string }) {
  return (
    <span className="inline-flex items-center gap-1.5" title={title}>
      <User aria-hidden className="size-[0.8em] shrink-0" strokeWidth={2.25} />
      <span>
        <HeadCount count={count} unsure={unsure} format={compactNumber} />
      </span>
      <span className="sr-only">{count === 1 ? ' person' : ' people'}</span>
    </span>
  )
}
