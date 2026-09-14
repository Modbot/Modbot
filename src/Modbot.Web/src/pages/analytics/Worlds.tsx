import { useCallback, useState } from 'react'
import { DailyLine, Legend, compactNumber, dateTime, minutes, nextSlot } from '@/components/charts'
import { WorldLink } from '@/components/facts'
import { api } from '@/lib/api'
import { CoverageNote, Nothing, PageMessage, Panel, RangePicker, Stat } from './shared'
import { useAnalytics, type Range } from './useAnalytics'

/** Below this many presence reports in the range, the numbers are shown but called thin. */
const THIN = 200

/**
 * Worlds -- which of our worlds actually get used? (spec 10.1)
 *
 * Time and visitors come from the desktop client's presence reports, which exist only while a
 * moderator's client is in the instance. A world nobody with the client visited reads as empty
 * however busy it was, and the page says so rather than letting a zero pass as a measurement.
 * Instances opened per world come from the audit log and are complete.
 *
 * Worlds are shown by the name the world sweep stored, with the id underneath, and each opens its
 * own popup. Nothing here asks VRChat for a name on page load (spec 4.3.4).
 */
export function Worlds() {
  const [range, setRange] = useState<Range>(30)
  const load = useCallback((q: string) => api.worldsAnalytics(q), [])
  const { data, error } = useAnalytics(load, range)

  if (error) return <PageMessage>{error}</PageMessage>

  const totalMinutes = data ? data.worlds.reduce((s, w) => s + w.minutesSeen, 0) : 0
  const totalVisitors = data ? data.worlds.reduce((s, w) => s + w.visitors, 0) : 0

  return (
    <div className="flex flex-col gap-4">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <>
          {data.presenceReports === 0 ? (
            <PageMessage>
              <div className="font-medium text-foreground">No presence reports in this range</div>
              <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
                Time in worlds and visitors come from the desktop client. They fill in once a moderator runs it in one of
                the group's instances. Instances opened per world still come from the audit log, below.
              </p>
            </PageMessage>
          ) : data.presenceReports < THIN ? (
            <PageMessage>
              <div className="font-medium text-foreground">Thin data</div>
              <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
                Only {compactNumber(data.presenceReports)} presence reports in this range. The numbers below are real
                but cover little; they firm up as moderators spend more time in instances with the client running.
              </p>
            </PageMessage>
          ) : null}

          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Stat label="Worlds" value={compactNumber(data.worlds.length)} note="with anything recorded in this range" />
            <Stat label="Time seen" value={minutes(totalMinutes)} note="people-time while a client was watching" />
            <Stat label="Visitors" value={compactNumber(totalVisitors)} note="distinct people per world, summed" />
            <Stat label="Presence reports" value={compactNumber(data.presenceReports)} note="from the desktop client" />
          </div>

          <Panel
            title="Worlds, by time people were seen in them"
            source="Time and visitors from the fact log; instances opened from daily totals"
            note="A person's time runs from when a client first saw them to when it saw them leave — or to the last report from that instance, when nobody saw them leave. Time nobody was watching is not counted. World names are not recorded yet, so worlds are shown by id."
          >
            {data.worlds.length === 0 ? (
              <Nothing>Worlds appear here once an instance is opened in one, or somebody with the client visits one.</Nothing>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                  <thead className="text-left text-muted-foreground">
                    <tr>
                      <th className="py-1 pr-3 font-medium">World</th>
                      <th className="py-1 pr-3 text-right font-medium">Holds</th>
                      <th className="py-1 pr-3 text-right font-medium">Time seen</th>
                      <th className="py-1 pr-3 text-right font-medium">Visitors</th>
                      <th className="py-1 pr-3 text-right font-medium">Arrivals seen</th>
                      <th className="py-1 pr-3 text-right font-medium">Instances opened</th>
                      <th className="py-1 font-medium">Last seen</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.worlds.map((w) => (
                      <tr key={w.worldId} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                        {/*
                          The name where there is one, with the id underneath rather than instead:
                          a moderator matching this against what they see in game needs the id, and
                          a world Modbot has not read yet has nothing else to show.
                        */}
                        <td className="py-1 pr-3" title={w.worldId}>
                          <div className="flex items-center gap-2">
                            {w.thumbnailImageUrl && (
                              <img
                                src={w.thumbnailImageUrl}
                                alt=""
                                loading="lazy"
                                className="size-8 shrink-0 rounded object-cover"
                              />
                            )}
                            <div className="min-w-0">
                              <div className="truncate">
                                {/* Opens the world, so the table is not a dead end showing ids. */}
                                <WorldLink id={w.worldId} name={w.name} />
                              </div>
                              <div
                                className="truncate font-mono text-muted-foreground"
                                style={{ fontSize: 'var(--text-tiny, 11px)' }}
                              >
                                {w.authorName ? `by ${w.authorName} · ` : ''}
                                {w.worldId}
                              </div>
                            </div>
                          </div>
                        </td>
                        <td className="py-1 pr-3 text-right tabular-nums text-muted-foreground">
                          {w.capacity ?? '—'}
                        </td>
                        <td className="py-1 pr-3 text-right tabular-nums">{w.minutesSeen > 0 ? minutes(w.minutesSeen) : '—'}</td>
                        <td className="py-1 pr-3 text-right tabular-nums">{compactNumber(w.visitors)}</td>
                        <td className="py-1 pr-3 text-right tabular-nums">{compactNumber(w.visits)}</td>
                        <td className="py-1 pr-3 text-right tabular-nums">{compactNumber(w.instancesOpened)}</td>
                        <td className="py-1 text-muted-foreground">{w.lastSeenAt ? dateTime(w.lastSeenAt) : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>

          <Panel
            title="Visitors per day, busiest worlds"
            source="From daily totals"
            note="Distinct people seen in each world per day, for the worlds with the most visitors in this range. Counts people, not reports: two moderators in the same room do not double it."
          >
            {data.visitorsPerDay.length === 0 ? (
              <Nothing>This fills in from presence reports once the daily totals have folded them in.</Nothing>
            ) : (
              <>
                <Legend items={data.visitorsPerDay.map((s, i) => ({ label: worldLabel(data.worlds, s.worldId), slot: nextSlot(i) }))} />
                <div className="mt-2">
                  <DailyLine
                    from={data.from}
                    to={data.to}
                    mode="zero"
                    series={data.visitorsPerDay.map((s, i) => ({ key: s.worldId, label: worldLabel(data.worlds, s.worldId), points: s.points, slot: nextSlot(i) }))}
                  />
                </div>
              </>
            )}
          </Panel>

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </>
      )}
    </div>
  )
}

/**
 * What to call a world on a chart.
 *
 * Reads the name out of the same table the rows are drawn from, rather than fetching it a second
 * way, so the legend and the table can never disagree about which world is which. An unnamed
 * world keeps its id, shortened -- a legend is too narrow for the whole thing.
 */
function worldLabel(worlds: { worldId: string; name: string | null }[], worldId: string): string {
  const named = worlds.find((w) => w.worldId === worldId)?.name
  return named ?? (worldId.length > 18 ? `${worldId.slice(0, 18)}…` : worldId)
}
