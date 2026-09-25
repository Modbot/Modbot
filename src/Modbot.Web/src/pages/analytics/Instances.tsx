import { useCallback, useState } from 'react'
import { DailyBars, DailyLine, Heatmap, Legend, compactNumber, dateTime, longDay, minutes, percent } from '@/components/charts'
import { InstanceTable } from '@/components/InstanceTable'
import { api, type HourOfWeek, type InstancePeaks } from '@/lib/api'
import { InstanceActivityChart } from './InstanceActivityChart'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { CoverageNote, PageMessage, Panel, RangePicker, Stat, StatStrip, Toggle } from './shared'
import { useAnalytics, type Range } from './useAnalytics'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const HOURS = Array.from({ length: 24 }, (_, h) => `${h}:00`)

/** Below this many presence reports in the range, the presence panels are shown but called thin. */
const THIN_REPORTS = 200

/**
 * Instances -- when is the community actually active? (spec 10.1)
 *
 * Opened and closed per day come from the daily totals. How long instances stay open and how many
 * are open at once come from the fact log. The heatmap is the cyclic answer spec 5.10 says a daily
 * series cannot give: "Tuesdays at 8pm are our busiest hour" is only visible once the days are laid
 * over each other.
 *
 * The peaks and the activity line come from a third source, VRChat's own head counts, which need no
 * moderator's companion and so cover instances presence reports cannot see. Their coverage is its
 * own figure and is marked when it is thin; the presence panels carry the report count for the same
 * reason, the same way the Worlds page does.
 */
export function Instances() {
  const [range, setRange] = useState<Range>(30)
  const [layer, setLayer] = useState<'arrivals' | 'opened'>('arrivals')
  const load = useCallback((q: string) => api.instancesAnalytics(q), [])
  const { data, error } = useAnalytics(load, range)

  if (error) return <PageMessage>{error}</PageMessage>

  const sum = (points: { value: number }[]) => points.reduce((s, p) => s + p.value, 0)
  const max = (points: { value: number }[]) => points.reduce((m, p) => Math.max(m, p.value), 0)

  return (
    <div className="flex flex-col gap-3">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <PanelGrid className="grid-cols-1">
          <StatStrip>
            <Stat label="Opened" value={compactNumber(sum(data.opened))} />
            <Stat label="Closed" value={compactNumber(sum(data.closed))} />
            <Stat
              label="Typical time open"
              value={data.typicalMinutesOpen === null ? '—' : minutes(data.typicalMinutesOpen)}
            />
            <Stat label="Most open at once" value={compactNumber(max(data.mostOpenAtOnce))} />
          </StatStrip>

          <Peaks peaks={data.peaks} />

          {/*
            Before the charts, deliberately. The counts answer "is the community active"; this
            answers "what actually ran last night", which is the question a moderator opening the
            page usually came with -- and a page of numbers with no instances on it cannot answer it.
          */}
          {data.openNow.length > 0 && (
            <Panel title="Open right now" flush>
              <InstanceTable instances={data.openNow} />
            </Panel>
          )}

          <Panel title="Recent instances" flush>
            {data.recent.length === 0 ? <EmptyRow>No instances yet.</EmptyRow> : <InstanceTable instances={data.recent} />}
          </Panel>

          <InstanceActivityChart />

          {data.presenceReports > 0 && data.presenceReports < THIN_REPORTS && (
            <PageMessage>Only {compactNumber(data.presenceReports)} presence reports in this range.</PageMessage>
          )}

          <Panel
            title={`When the community is active (${zoneLabel()})`}
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
              <EmptyRow>
                {layer === 'arrivals' ? 'No arrivals seen in this range.' : 'No instances opened in this range.'}
              </EmptyRow>
            ) : (
              <Heatmap
                rows={DAYS}
                cols={HOURS}
                values={toLocalGrid(data.hourOfWeek[layer])}
                valueLabel={layer === 'arrivals' ? 'arrivals' : 'instances opened'}
                slot={layer === 'arrivals' ? 1 : 4}
              />
            )}
          </Panel>

          <PanelGrid className="lg:grid-cols-2">
            <Panel title="Opened and closed per day">
              <Legend items={[{ label: 'Opened', slot: 1 }, { label: 'Closed', slot: 2 }]} />
              <div className="mt-2">
                <DailyBars
                  from={data.from}
                  to={data.to}
                  series={[
                    { key: 'opened', label: 'opened', points: data.opened, slot: 1 },
                    { key: 'closed', label: 'closed', points: data.closed, slot: 2 },
                  ]}
                />
              </div>
            </Panel>

            <Panel title="Most open at once, per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'open', label: 'open at once', points: data.mostOpenAtOnce, slot: 4 }]}
              />
            </Panel>
          </PanelGrid>

          <PanelGrid className="lg:grid-cols-2">
            <Panel title="Most people at once, per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'people', label: 'people', points: data.peaks.mostPeopleAtOncePerDay, slot: 3 }]}
              />
            </Panel>

            {/*
              People-time, so a long steady evening outweighs a short rush. Hours rather than
              minutes on the axis: a busy day is thousands of people-minutes and a chart of
              thousands is a chart nobody reads.
            */}
            <Panel title="Busy time per day">
              <DailyLine
                from={data.from}
                to={data.to}
                series={[{ key: 'busy', label: 'people-hours', points: toHours(data.peaks.peopleMinutesPerDay), slot: 1 }]}
                format={(v) => `${compactNumber(v)} h`}
              />
            </Panel>
          </PanelGrid>

          <PanelGrid className="lg:grid-cols-2">
            <Panel title="Typical time open, per day">
              <DailyLine
                from={data.from}
                to={data.to}
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
                  series={[{ key: 'people', label: 'people', points: data.mostPeopleInOne, slot: 2 }]}
                />
              )}
            </Panel>
          </PanelGrid>

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </PanelGrid>
      )}
    </div>
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

  return (
    <>
      {coverage.instancesOpen > 0 && coverage.instancesCounted === 0 ? (
        <PageMessage>No head counts in this range.</PageMessage>
      ) : coverage.thin ? (
        <PageMessage>
          Head counts cover {percent(coverage.minutesCounted, coverage.minutesInstancesWereOpen)} of the time
          instances were open.
        </PageMessage>
      ) : null}

      <StatStrip>
        <Stat
          label="Most people at once"
          value={peaks.mostPeopleAtOnce ? compactNumber(peaks.mostPeopleAtOnce.value) : '—'}
          note={peaks.mostPeopleAtOnce ? dateTime(peaks.mostPeopleAtOnce.at) : undefined}
        />
        <Stat
          label="Fullest instance"
          value={peaks.busiestInstance ? compactNumber(peaks.busiestInstance.people) : '—'}
          note={
            peaks.busiestInstance
              ? `${peaks.busiestInstance.worldName ?? peaks.busiestInstance.worldId} · ${dateTime(peaks.busiestInstance.at)}`
              : undefined
          }
        />
        <Stat
          label="Busiest day"
          value={peaks.busiestDay ? minutes(peaks.busiestDay.peopleMinutes) : '—'}
          note={peaks.busiestDay ? longDay(peaks.busiestDay.day) : undefined}
        />
        <Stat
          label="Busiest hour"
          value={peaks.busiestHour ? minutes(peaks.busiestHour.peopleMinutes) : '—'}
          note={peaks.busiestHour ? dateTime(peaks.busiestHour.startedAt) : undefined}
        />
      </StatStrip>
    </>
  )
}

/**
 * Shifts the 168 UTC buckets into the viewer's clock and lays them out by day.
 *
 * Whole hours only. A half-hour zone lands half an hour off, which is stated on the panel; the
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

function zoneLabel(): string {
  const offset = -new Date().getTimezoneOffset()
  const sign = offset >= 0 ? '+' : '−'
  const h = Math.floor(Math.abs(offset) / 60)
  const m = Math.abs(offset) % 60
  return `UTC${sign}${h}${m ? `:${String(m).padStart(2, '0')}` : ''}`
}
