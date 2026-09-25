import { useCallback, useState } from 'react'
import { DailyLine, Legend, compactNumber, dateTime, minutes, nextSlot } from '@/components/charts'
import { WorldLink } from '@/components/facts'
import { api } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { CoverageNote, PageMessage, Panel, RangePicker, Stat, StatStrip } from './shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { useAnalytics, type Range } from './useAnalytics'
import { vrchatMedia } from '@/lib/vrchatMedia'

/** Below this many presence reports in the range, the numbers are shown but called thin. */
const THIN = 200

/**
 * Worlds -- which of our worlds actually get used? (spec 10.1)
 *
 * Time and visitors come from the companion's presence reports, which exist only while a
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
    <div className="flex flex-col gap-3">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <PanelGrid className="grid-cols-1">
          {data.presenceReports === 0 ? (
            <PageMessage>No presence reports in this range.</PageMessage>
          ) : data.presenceReports < THIN ? (
            <PageMessage>
              Only <span className="font-mono">{compactNumber(data.presenceReports)}</span> presence reports in this range.
            </PageMessage>
          ) : null}

          <StatStrip>
            <Stat label="Worlds" value={compactNumber(data.worlds.length)} />
            <Stat label="Time seen" value={minutes(totalMinutes)} />
            <Stat label="Visitors" value={compactNumber(totalVisitors)} />
            <Stat label="Presence reports" value={compactNumber(data.presenceReports)} />
          </StatStrip>

          <Panel title="Worlds, by time people were seen in them" flush>
            {data.worlds.length === 0 ? (
              <EmptyRow>No worlds in this range.</EmptyRow>
            ) : (
              <Table
                pinFirst
                head={
                  <>
                    <Th>World</Th>
                    <Th className="text-right">Holds</Th>
                    <Th className="text-right">Time seen</Th>
                    <Th className="text-right">Visitors</Th>
                    <Th className="text-right">Arrivals seen</Th>
                    <Th className="text-right">Instances opened</Th>
                    <Th>Last seen</Th>
                  </>
                }
              >
                {data.worlds.map((w) => (
                  <Tr key={w.worldId}>
                    {/*
                      The name where there is one, with the id underneath rather than instead:
                      a moderator matching this against what they see in game needs the id, and
                      a world Modbot has not read yet has nothing else to show.
                    */}
                    <Td title={w.worldId}>
                      <div className="flex items-center gap-2">
                        {w.thumbnailImageUrl && (
                          <img
                            src={vrchatMedia(w.thumbnailImageUrl)}
                            alt=""
                            loading="lazy"
                            className="size-8 shrink-0 rounded-sm object-cover"
                          />
                        )}
                        <div className="min-w-0">
                          <div className="truncate">
                            {/* Opens the world, so the table is not a dead end showing ids. */}
                            <WorldLink id={w.worldId} name={w.name} />
                          </div>
                          <div
                            className="truncate text-muted-foreground"
                            style={{ fontSize: 'var(--text-tiny)' }}
                          >
                            {w.authorName ? `by ${w.authorName} · ` : ''}
                            <span className="font-mono">{w.worldId}</span>
                          </div>
                        </div>
                      </div>
                    </Td>
                    <Td className="text-right font-mono text-muted-foreground">{w.capacity ?? '—'}</Td>
                    <Td className="text-right font-mono">{w.minutesSeen > 0 ? minutes(w.minutesSeen) : '—'}</Td>
                    <Td className="text-right font-mono">{compactNumber(w.visitors)}</Td>
                    <Td className="text-right font-mono">{compactNumber(w.visits)}</Td>
                    <Td className="text-right font-mono">{compactNumber(w.instancesOpened)}</Td>
                    <Td className="font-mono text-muted-foreground">{w.lastSeenAt ? dateTime(w.lastSeenAt) : '—'}</Td>
                  </Tr>
                ))}
              </Table>
            )}
          </Panel>

          <Panel title="Visitors per day, busiest worlds" flush={data.visitorsPerDay.length === 0}>
            {data.visitorsPerDay.length === 0 ? (
              <EmptyRow>No data yet.</EmptyRow>
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
        </PanelGrid>
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
