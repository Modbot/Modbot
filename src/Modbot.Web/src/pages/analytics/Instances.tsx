import { useCallback, useState } from 'react'
import { DailyBars, Heatmap, Legend, compactNumber, minutes } from '@/components/charts'
import { InstanceTable } from '@/components/InstanceTable'
import { api, type HourOfWeek } from '@/lib/api'
import { CoverageNote, Nothing, PageMessage, Panel, RangePicker, Stat, Toggle } from './shared'
import { useAnalytics, type Range } from './useAnalytics'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const HOURS = Array.from({ length: 24 }, (_, h) => `${h}:00`)

/**
 * Instances -- when is the community actually active? (spec 10.1)
 *
 * Opened and closed per day come from the daily totals. How long instances stay open, how many
 * are open at once and how many people were in one come from the fact log. The heatmap is the
 * cyclic answer spec 5.10 says a daily series cannot give: "Tuesdays at 8pm are our busiest
 * hour" is only visible once the days are laid over each other.
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
    <div className="flex flex-col gap-4">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Stat label="Opened" value={compactNumber(sum(data.opened))} />
            <Stat label="Closed" value={compactNumber(sum(data.closed))} />
            <Stat
              label="Typical time open"
              value={data.typicalMinutesOpen === null ? '—' : minutes(data.typicalMinutesOpen)}
            />
            <Stat label="Most open at once" value={compactNumber(max(data.mostOpenAtOnce))} />
          </div>

          {/*
            Before the charts, deliberately. The counts answer "is the community active"; this
            answers "what actually ran last night", which is the question a moderator opening the
            page usually came with -- and a page of numbers with no instances on it cannot answer it.
          */}
          {data.openNow.length > 0 && (
            <Panel title="Open right now">
              <InstanceTable instances={data.openNow} />
            </Panel>
          )}

          <Panel title="Recent instances">
            {data.recent.length === 0 ? <Nothing>No instances yet.</Nothing> : <InstanceTable instances={data.recent} />}
          </Panel>

          <Panel
            title={`When the community is active (${zoneLabel()})`}
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
              <Nothing>
                {layer === 'arrivals' ? 'No arrivals seen in this range.' : 'No instances opened in this range.'}
              </Nothing>
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

          <div className="grid gap-4 lg:grid-cols-2">
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
          </div>

          <Panel title="Most people in one instance, per day">
            <DailyBars
              from={data.from}
              to={data.to}
              series={[{ key: 'people', label: 'people', points: data.mostPeopleInOne, slot: 3 }]}
            />
          </Panel>

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </>
      )}
    </div>
  )
}

const toPoints = (buckets: number[]) => buckets.map((value) => ({ value }))

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
