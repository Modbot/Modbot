import { useCallback, useState } from 'react'
import { DailyBars, DailyLine, Heatmap, RankedList, compactNumber, longDay, minutes, percent } from '@/components/charts'
import { PersonLink } from '@/components/facts'
import { api, type ServerContributor } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { CoverageNote, PageMessage, Panel, RangePicker, Stat, StatStrip, Toggle } from './shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { useAnalytics, type Range } from './useAnalytics'
import { plural } from '@/lib/format'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const HOURS = Array.from({ length: 24 }, (_, h) => `${h}:00`)

/**
 * My Server -- is the Discord server healthy, and who keeps it going? (M5 spec §6)
 *
 * Members, messages, voice and moderation per day come from the daily totals. "Active" is having
 * sent a message or been in voice, counted as distinct people over a day, a week and thirty days --
 * the server builds those, because distinct people cannot be summed from daily counts. New members
 * who stayed come from the fact log; member health is about now and ignores the range.
 *
 * Every person listed opens the person popup, as names do everywhere else.
 */
export function MyServer() {
  const [range, setRange] = useState<Range>(30)
  const [activeSpan, setActiveSpan] = useState<'daily' | 'weekly' | 'monthly'>('daily')
  const load = useCallback((q: string) => api.serverAnalytics(q), [])
  const { data, error } = useAnalytics(load, range)

  if (error) return <PageMessage tone="danger">{error}</PageMessage>

  const sum = (points: { value: number }[]) => points.reduce((s, p) => s + p.value, 0)
  const latestCount = data?.memberCount[data.memberCount.length - 1]
  const lastActive = data?.active[data.active.length - 1]

  return (
    <div className="flex flex-col gap-3">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <PanelGrid className="grid-cols-1">
          <StatStrip>
            <Stat
              label="Members"
              value={latestCount ? compactNumber(latestCount.value) : '—'}
              note={latestCount ? longDay(latestCount.day) : undefined}
              noteMono
            />
            <Stat label="Messages" value={compactNumber(sum(data.messages))} />
            <Stat
              label="Active in 30 days"
              value={compactNumber(data.health.activeLast30Days)}
              note={percent(data.health.activeLast30Days, data.health.members)}
              noteMono
            />
            <Stat label="Time in voice" value={minutes(sum(data.voiceMinutes))} />
          </StatStrip>

          <Panel title="Member count">
            <DailyLine
              from={data.from}
              to={data.to}
              mode="carry"
              zeroBased={false}
              series={[{ key: 'members', label: 'members', one: 'member', points: data.memberCount, slot: 1 }]}
            />
          </Panel>

          <PanelGrid className="lg:grid-cols-2">
            <Panel title="Joins and leaves per day">
              <DailyBars
                from={data.from}
                to={data.to}
                legend={[{ label: 'Joined', slot: 3 }, { label: 'Left', slot: 2 }]}
                series={[
                  { key: 'joined', label: 'joined', points: data.joined, slot: 3 },
                  { key: 'left', label: 'left', points: data.left, slot: 2 },
                ]}
              />
            </Panel>

            <Panel title="Messages per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'messages', label: 'messages', one: 'message', points: data.messages, slot: 1 }]}
              />
            </Panel>
          </PanelGrid>

          <PanelGrid className="lg:grid-cols-2">
            <Panel
              title="Active members"
              flush
              right={
                <Toggle
                  value={activeSpan}
                  onChange={setActiveSpan}
                  options={[
                    { value: 'daily', label: 'Day' },
                    { value: 'weekly', label: '7 days' },
                    { value: 'monthly', label: '30 days' },
                  ]}
                />
              }
            >
              <div className="p-(--panel-pad)">
                <DailyLine
                  from={data.from}
                  to={data.to}
                  series={[
                    {
                      key: 'active',
                    label: 'active',
                      points: data.active.map((a) => ({ day: a.day, value: a[activeSpan] })),
                      slot: 4,
                    },
                  ]}
                />
              </div>
              {lastActive && (
                <StatStrip className="m-0 grid-cols-3 xl:grid-cols-3">
                  <Stat label="Day" value={compactNumber(lastActive.daily)} />
                  <Stat label="7 days" value={compactNumber(lastActive.weekly)} />
                  <Stat label="30 days" value={compactNumber(lastActive.monthly)} />
                </StatStrip>
              )}
            </Panel>

            <Panel title="Minutes in voice per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'voice', label: 'minutes', one: 'minute', points: data.voiceMinutes, slot: 5 }]}
              />
            </Panel>
          </PanelGrid>

          <PanelGrid className="lg:grid-cols-2">
            <Panel title="Busiest channels" flush={data.busiestChannels.length === 0}>
              {data.busiestChannels.length === 0 ? (
                <EmptyRow>No messages in this range.</EmptyRow>
              ) : (
                <RankedList
                  slot={1}
                  rows={data.busiestChannels.map((c) => ({
                    key: c.id,
                    label: c.name ? `#${c.name}` : c.id,
                    value: c.messages,
                  }))}
                />
              )}
            </Panel>

            <Panel title={`Busiest hours (${zoneLabel()})`} flush={data.hourOfWeek.messages.every((v) => v === 0)}>
              {data.hourOfWeek.messages.every((v) => v === 0) ? (
                <EmptyRow>No messages in this range.</EmptyRow>
              ) : (
                <Heatmap
                  rows={DAYS}
                  cols={HOURS}
                  values={toLocalGrid(data.hourOfWeek.messages)}
                  valueLabel="messages"
                  valueLabelOne="message"
                  slot={1}
                />
              )}
            </Panel>
          </PanelGrid>

          <Panel title="Moderation actions per day">
            <DailyBars
              from={data.from}
              to={data.to}
              stacked
              legend={[
                { label: 'Bans', slot: 2 },
                { label: 'Kicks', slot: 3 },
                { label: 'Timeouts', slot: 4 },
                { label: 'Messages removed', slot: 5 },
              ]}
              series={[
                { key: 'bans', label: 'bans', one: 'ban', points: data.bans, slot: 2 },
                { key: 'kicks', label: 'kicks', one: 'kick', points: data.kicks, slot: 3 },
                { key: 'timeouts', label: 'timeouts', one: 'timeout', points: data.timeouts, slot: 4 },
                { key: 'removed', label: 'messages removed', one: 'message removed', points: data.messagesRemoved, slot: 5 },
              ]}
            />
          </Panel>

          <Panel title="New members still here" flush>
            <Table
              head={
                <>
                  <Th>After</Th>
                  <Th className="text-right">Joined</Th>
                  <Th className="text-right">Still here</Th>
                  <Th className="text-right">Still active</Th>
                </>
              }
            >
              {data.newMembers.map((n) => (
                <Tr key={n.days}>
                  <Td>{n.days} {plural(n.days, 'day')}</Td>
                  <Td className="text-right font-mono">{compactNumber(n.joined)}</Td>
                  <Td className="text-right font-mono">
                    {compactNumber(n.stillHere)} <span className="text-muted-foreground">{percent(n.stillHere, n.joined)}</span>
                  </Td>
                  <Td className="text-right font-mono">
                    {compactNumber(n.stillActive)} <span className="text-muted-foreground">{percent(n.stillActive, n.joined)}</span>
                  </Td>
                </Tr>
              ))}
            </Table>
          </Panel>

          <PanelGrid className="lg:grid-cols-2">
            <Panel title="Member health" flush>
              {/* The margin is the room for the strip's lower line, which the table's head would cover. */}
              <StatStrip className="m-0 mb-(--hairline) grid-cols-3 xl:grid-cols-3">
                <Stat label="Members" value={compactNumber(data.health.members)} />
                <Stat
                  label="Active in 30 days"
                  value={percent(data.health.activeLast30Days, data.health.members)}
                />
                <Stat label="Went quiet" value={compactNumber(data.health.wentQuiet)} />
              </StatStrip>
              {data.health.quiet.length === 0 ? (
                <EmptyRow>Nobody went quiet.</EmptyRow>
              ) : (
                <PeopleTable people={data.health.quiet} />
              )}
            </Panel>

            <Panel title="Top contributors" flush>
              {data.topContributors.length === 0 ? (
                <EmptyRow>No messages in this range.</EmptyRow>
              ) : (
                <PeopleTable people={data.topContributors} />
              )}
            </Panel>
          </PanelGrid>

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </PanelGrid>
      )}
    </div>
  )
}

/** Everybody here is a Discord member, so every name opens the Discord person popup. */
function PeopleTable({ people }: { people: ServerContributor[] }) {
  return (
    <Table
      head={
        <>
          <Th>Person</Th>
          <Th className="text-right">Messages</Th>
          <Th className="text-right">Voice</Th>
        </>
      }
    >
      {people.map((p) => (
        <Tr key={p.who.id}>
          <Td>
            <PersonLink platform={p.who.platform} id={p.who.id} name={p.who.name} />
          </Td>
          <Td className="text-right font-mono">{compactNumber(p.messages)}</Td>
          <Td className="text-right font-mono text-muted-foreground">
            {p.voiceMinutes > 0 ? minutes(p.voiceMinutes) : '—'}
          </Td>
        </Tr>
      ))}
    </Table>
  )
}

/** Shifts the 168 UTC buckets into the viewer's clock and lays them out by day, as on the Instances page. */
function toLocalGrid(buckets: number[]): number[][] {
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
