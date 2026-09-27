import { Fragment, useCallback, useState } from 'react'
import { DailyLine, compactNumber, dateTime, minutes, nextSlot } from '@/components/charts'
import { WorldLink } from '@/components/facts'
import { HeadCount } from '@/components/HeadCount'
import { api, type WorldSummary } from '@/lib/api'
import { plural } from '@/lib/format'
import { openWorld } from '@/lib/subject'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { CoverageLine, PageMessage, Panel, RangePicker, Stat, StatStrip } from './shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { useAnalytics, type Range } from './useAnalytics'
import { vrchatMedia } from '@/lib/vrchatMedia'

/** Below this many presence reports in the range, the numbers are shown but called thin. */
const THIN = 200

/**
 * Worlds -- which of our worlds actually get used? (spec 10.1)
 *
 * Instances, time open and most at once come from the group's own instances, which Modbot keeps
 * whether or not anybody from the team was in them, so they lead and they decide the order. Time
 * seen and visitors come from the companion's presence reports, which exist only while a
 * moderator's client is in the instance: a world nobody with the client visited reads as empty
 * there however busy it was. "Instances opened" is the audit log's count, and can be higher than
 * "Instances": it also counts creates Modbot never saw as an instance.
 *
 * Worlds are shown by the name the world sweep stored, with the id underneath, and each row opens
 * the world's popup. On a phone and in VR the table becomes two-line rows (index.css,
 * `data-layout`). Nothing here asks VRChat for a name on page load (spec 4.3.4).
 */
export function Worlds() {
  const [range, setRange] = useState<Range>(30)
  const load = useCallback((q: string) => api.worldsAnalytics(q), [])
  const { data, error } = useAnalytics(load, range)

  if (error) return <PageMessage tone="danger">{error}</PageMessage>

  const totalMinutes = data ? data.worlds.reduce((s, w) => s + w.minutesSeen, 0) : 0
  const totalVisitors = data ? data.worlds.reduce((s, w) => s + w.visitors, 0) : 0

  // These two tiles are the sums of the table's own columns, so the tiles and the rows cannot
  // disagree.
  const totalInstances = data ? data.worlds.reduce((s, w) => s + w.instances, 0) : 0
  const totalMinutesOpen = data ? data.worlds.reduce((s, w) => s + w.minutesOpen, 0) : 0
  const longestOpen = data ? Math.max(0, ...data.worlds.map((w) => w.minutesOpen)) : 0

  return (
    <div className="flex flex-col gap-3">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <PanelGrid className="grid-cols-1">
          <CoverageLine coverage={data.coverage} generatedAt={data.generatedAt} />

          {data.presenceReports === 0 ? (
            <PageMessage>No presence reports in this range.</PageMessage>
          ) : data.presenceReports < THIN ? (
            <PageMessage>
              Only <span className="font-mono">{compactNumber(data.presenceReports)}</span> presence reports in this range.
            </PageMessage>
          ) : null}

          <StatStrip className="md:grid-cols-3 xl:grid-cols-6">
            <Stat label="Worlds" value={compactNumber(data.worlds.length)} />
            <Stat label="Instances" value={compactNumber(totalInstances)} />
            <Stat label="Time open" value={totalMinutesOpen > 0 ? minutes(totalMinutesOpen) : '—'} />
            <Stat label="Time seen" value={minutes(totalMinutes)} />
            <Stat label="Visitors" value={compactNumber(totalVisitors)} />
            <Stat label="Presence reports" value={compactNumber(data.presenceReports)} />
          </StatStrip>

          <Panel title="Worlds, by time open" flush>
            {data.worlds.length === 0 ? (
              <EmptyRow>No worlds in this range.</EmptyRow>
            ) : (
              <>
              {/* Two-line rows on a phone and in VR, where the table's columns ran off the side. */}
              <ul data-layout="narrow">
                {data.worlds.map((w) => (
                  <li key={w.worldId} className="border-t border-(length:--hairline) first:border-t-0">
                    <button
                      type="button"
                      onClick={() => openWorld(w.worldId)}
                      className="flex w-full items-center gap-2 px-(--panel-pad) py-2 text-left hover:bg-muted/40"
                      style={{ minHeight: 'var(--row-h)' }}
                    >
                      {w.thumbnailImageUrl && (
                        <img
                          src={vrchatMedia(w.thumbnailImageUrl)}
                          alt=""
                          loading="lazy"
                          className="size-10 shrink-0 object-cover"
                        />
                      )}
                      <div className="min-w-0">
                        <div className="truncate">{worldLabel(data.worlds, w.worldId)}</div>
                        <div className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                          {secondLine(w)}
                        </div>
                      </div>
                    </button>
                  </li>
                ))}
              </ul>
              <div data-layout="wide">
              <Table
                pinFirst
                head={
                  <>
                    <Th>World</Th>
                    <Th className="text-right">Instances</Th>
                    <Th className="text-right">Time open</Th>
                    <Th className="text-right">Most at once</Th>
                    <Th className="text-right">Holds</Th>
                    <Th className="text-right">Time seen</Th>
                    <Th className="text-right">Visitors</Th>
                    <Th className="text-right">Arrivals seen</Th>
                    <Th className="text-right">Instances opened</Th>
                    <Th>Last opened</Th>
                    <Th>Last seen</Th>
                  </>
                }
              >
                {data.worlds.map((w) => (
                  <Tr
                    key={w.worldId}
                    className="cursor-pointer hover:bg-muted/40"
                    onClick={(e) => {
                      // The world's name is a link of its own; the rest of the row opens the same world.
                      if ((e.target as HTMLElement).closest('button, a')) return
                      openWorld(w.worldId)
                    }}
                  >
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
                            className="size-8 shrink-0 object-cover"
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
                    <Td className="text-right font-mono">{compactNumber(w.instances)}</Td>
                    <Td className="text-right font-mono">
                      <div className="flex items-center justify-end gap-2">
                        {/* One scale for every row, so the bars compare worlds. */}
                        <div className="h-1.5 w-16 shrink-0 bg-muted">
                          <div
                            className="h-full bg-primary"
                            style={{ width: longestOpen > 0 ? `${(w.minutesOpen / longestOpen) * 100}%` : 0 }}
                          />
                        </div>
                        <span className="whitespace-nowrap">{w.minutesOpen > 0 ? minutes(w.minutesOpen) : '—'}</span>
                      </div>
                    </Td>
                    <Td className="text-right font-mono whitespace-nowrap">
                      {w.mostAtOnce === null ? '—' : <MostAtOnce world={w} />}
                    </Td>
                    <Td className="text-right font-mono text-muted-foreground">{w.capacity ?? '—'}</Td>
                    <Td className="text-right font-mono">{w.minutesSeen > 0 ? minutes(w.minutesSeen) : '—'}</Td>
                    <Td className="text-right font-mono">{compactNumber(w.visitors)}</Td>
                    <Td className="text-right font-mono">{compactNumber(w.visits)}</Td>
                    <Td className="text-right font-mono">{compactNumber(w.instancesOpened)}</Td>
                    <Td className="font-mono text-muted-foreground">{w.lastOpenedAt ? dateTime(w.lastOpenedAt) : '—'}</Td>
                    <Td className="font-mono text-muted-foreground">{w.lastSeenAt ? dateTime(w.lastSeenAt) : '—'}</Td>
                  </Tr>
                ))}
              </Table>
              </div>
              </>
            )}
          </Panel>

          <Panel title="Visitors per day, busiest worlds">
            <DailyLine
              from={data.from}
              to={data.to}
              missing={data.daysWithoutPresenceReports}
              today={data.today}
              mode="zero"
              emptyText="No data yet."
              legend={
                data.visitorsPerDay.length > 1
                  ? data.visitorsPerDay.map((s, i) => ({ label: worldLabel(data.worlds, s.worldId), slot: nextSlot(i) }))
                  : undefined
              }
              series={data.visitorsPerDay.map((s, i) => ({ key: s.worldId, label: worldLabel(data.worlds, s.worldId), points: s.points, slot: nextSlot(i) }))}
            />
          </Panel>

        </PanelGrid>
      )}
    </div>
  )
}

/** "53 of 80": the peak against what the world holds, so the number has something to be read against. */
function MostAtOnce({ world }: { world: WorldSummary }) {
  if (world.mostAtOnce === null) return <>—</>
  return (
    <>
      <HeadCount count={world.mostAtOnce} unsure={world.mostAtOnceUnsure} />
      {world.capacity !== null && <span className="text-muted-foreground"> of {world.capacity}</span>}
    </>
  )
}

/** A world's second line on a phone or in VR: "2 instances · 10 h 50 min open · 53 of 80". */
function secondLine(w: WorldSummary): React.ReactNode {
  const parts: React.ReactNode[] = [`${compactNumber(w.instances)} ${plural(w.instances, 'instance')}`]
  if (w.minutesOpen > 0) parts.push(`${minutes(w.minutesOpen)} open`)
  if (w.mostAtOnce !== null) parts.push(<MostAtOnce key="most" world={w} />)
  return parts.map((p, i) => (
    <Fragment key={i}>
      {i > 0 && ' · '}
      {p}
    </Fragment>
  ))
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
