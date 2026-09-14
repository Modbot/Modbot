import { useCallback, useState } from 'react'
import { DailyBars, Heatmap, Legend, compactNumber, minutes } from '@/components/charts'
import { RoomTable } from '@/components/RoomTable'
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
            <Stat label="Opened" value={compactNumber(sum(data.opened))} note="group instances, in this range" />
            <Stat label="Closed" value={compactNumber(sum(data.closed))} note="closed by a moderator — VRChat records no other kind" />
            <Stat
              label="Typical time open"
              value={data.typicalMinutesOpen === null ? '—' : minutes(data.typicalMinutesOpen)}
              note={
                data.instancesWithBothEnds === 0
                  ? 'needs instances with both an open and a close'
                  : `middle value over ${compactNumber(data.instancesWithBothEnds)} with both ends recorded`
              }
            />
            <Stat label="Most open at once" value={compactNumber(max(data.mostOpenAtOnce))} note="on the busiest day in this range" />
          </div>

          {/*
            Before the charts, deliberately. The counts answer "is the community active"; this
            answers "what actually ran last night", which is the question a moderator opening the
            page usually came with -- and a page of numbers with no rooms on it cannot answer it.
          */}
          {data.openNow.length > 0 && (
            <Panel
              title="Open right now"
              source="From the group's own instance list"
              note="Polled every ten seconds, so this includes rooms nobody from the moderation team is standing in. Not filtered by the date range above: a room that opened before it is still open now."
            >
              <RoomTable rooms={data.openNow} />
            </Panel>
          )}

          <Panel
            title="Recent instances"
            source="From the group's own instance list"
            note="Newest first. A room ends exactly when it leaves the group's live list; one marked “went quiet” merely stopped being seen for long enough to count as finished, which is a weaker claim."
          >
            {data.recent.length === 0 ? (
              <Nothing>
                Rooms appear here once the group opens one. Modbot polls the group's instance list
                every ten seconds, so it sees them whether or not anybody is in them.
              </Nothing>
            ) : (
              <RoomTable rooms={data.recent} />
            )}
          </Panel>

          <Panel
            title="When the community is active"
            source={layer === 'arrivals' ? 'From the fact log — arrivals seen by the desktop client' : 'From the fact log — instances opened, per the audit log'}
            note={`Every hour of every day of the week in this range, laid over each other. Shown in your own time zone (${zoneLabel()}), rounded to the hour. Arrivals are only seen while a moderator's client is in the instance; openings come from the audit log and are complete.`}
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
                {layer === 'arrivals'
                  ? 'No arrivals seen in this range. This fills in once a moderator runs the desktop client in a group instance.'
                  : 'No instances opened in this range.'}
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
            <Panel title="Opened and closed per day" source="From daily totals">
              <Legend items={[{ label: 'Opened', slot: 1 }, { label: 'Closed', slot: 2 }]} />
              <div className="mt-2">
                <DailyBars
                  from={data.from}
                  to={data.to}
                  series={[
                    { key: 'opened', label: 'opened', points: data.opened, slot: 1 },
                    { key: 'closed', label: 'closed', points: data.closed, slot: 2 },
                  ]}
                  emptyText="Instances appear here as the audit log records them being opened and closed."
                />
              </div>
            </Panel>

            <Panel
              title="Most open at once, per day"
              source="From the fact log"
              note="An instance with no close on record counts as open until the last thing Modbot saw happen in it — VRChat only records a close when a moderator closes the instance."
            >
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'open', label: 'open at once', points: data.mostOpenAtOnce, slot: 4 }]}
                emptyText="This fills in as instances are opened."
              />
            </Panel>
          </div>

          <Panel
            title="Most people in one instance, per day"
            source="From the fact log — the desktop client's presence reports"
            note="The biggest population known in any single instance that day. Only instances a moderator's client was in are counted, so a full instance nobody with the client visited reads as nothing."
          >
            <DailyBars
              from={data.from}
              to={data.to}
              series={[{ key: 'people', label: 'people', points: data.mostPeopleInOne, slot: 3 }]}
              emptyText="This fills in once a moderator runs the desktop client in a group instance."
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
