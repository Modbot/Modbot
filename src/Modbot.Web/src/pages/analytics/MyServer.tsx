import { useCallback, useState } from 'react'
import { DailyBars, DailyLine, Heatmap, Legend, RankedList, compactNumber, longDay, minutes, percent } from '@/components/charts'
import { PersonLink } from '@/components/facts'
import { api, type ServerContributor } from '@/lib/api'
import { CoverageNote, Nothing, PageMessage, Panel, RangePicker, Stat, Toggle } from './shared'
import { useAnalytics, type Range } from './useAnalytics'

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

  if (error) return <PageMessage>{error}</PageMessage>

  const sum = (points: { value: number }[]) => points.reduce((s, p) => s + p.value, 0)
  const latestCount = data?.memberCount[data.memberCount.length - 1]
  const lastActive = data?.active[data.active.length - 1]

  return (
    <div className="flex flex-col gap-4">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Stat
              label="Members"
              value={latestCount ? compactNumber(latestCount.value) : '—'}
              note={latestCount ? longDay(latestCount.day) : undefined}
            />
            <Stat label="Messages" value={compactNumber(sum(data.messages))} />
            <Stat
              label="Active in 30 days"
              value={compactNumber(data.health.activeLast30Days)}
              note={percent(data.health.activeLast30Days, data.health.members)}
            />
            <Stat label="Time in voice" value={minutes(sum(data.voiceMinutes))} />
          </div>

          <Panel title="Member count">
            <DailyLine
              from={data.from}
              to={data.to}
              mode="carry"
              zeroBased={false}
              series={[{ key: 'members', label: 'members', points: data.memberCount, slot: 1 }]}
            />
          </Panel>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel title="Joins and leaves per day">
              <Legend items={[{ label: 'Joined', slot: 3 }, { label: 'Left', slot: 2 }]} />
              <div className="mt-2">
                <DailyBars
                  from={data.from}
                  to={data.to}
                  series={[
                    { key: 'joined', label: 'joined', points: data.joined, slot: 3 },
                    { key: 'left', label: 'left', points: data.left, slot: 2 },
                  ]}
                />
              </div>
            </Panel>

            <Panel title="Messages per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'messages', label: 'messages', points: data.messages, slot: 1 }]}
              />
            </Panel>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel
              title="Active members"
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
              {lastActive && (
                <div className="mt-2 grid grid-cols-3 gap-3">
                  <Stat label="Day" value={compactNumber(lastActive.daily)} />
                  <Stat label="7 days" value={compactNumber(lastActive.weekly)} />
                  <Stat label="30 days" value={compactNumber(lastActive.monthly)} />
                </div>
              )}
            </Panel>

            <Panel title="Minutes in voice per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'voice', label: 'minutes', points: data.voiceMinutes, slot: 5 }]}
              />
            </Panel>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel title="Busiest channels">
              {data.busiestChannels.length === 0 ? (
                <Nothing>No messages in this range.</Nothing>
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

            <Panel title={`Busiest hours (${zoneLabel()})`}>
              {data.hourOfWeek.messages.every((v) => v === 0) ? (
                <Nothing>No messages in this range.</Nothing>
              ) : (
                <Heatmap
                  rows={DAYS}
                  cols={HOURS}
                  values={toLocalGrid(data.hourOfWeek.messages)}
                  valueLabel="messages"
                  slot={1}
                />
              )}
            </Panel>
          </div>

          <Panel title="Moderation actions per day">
            <Legend
              items={[
                { label: 'Bans', slot: 2 },
                { label: 'Kicks', slot: 3 },
                { label: 'Timeouts', slot: 4 },
                { label: 'Messages removed', slot: 5 },
              ]}
            />
            <div className="mt-2">
              <DailyBars
                from={data.from}
                to={data.to}
                stacked
                series={[
                  { key: 'bans', label: 'bans', points: data.bans, slot: 2 },
                  { key: 'kicks', label: 'kicks', points: data.kicks, slot: 3 },
                  { key: 'timeouts', label: 'timeouts', points: data.timeouts, slot: 4 },
                  { key: 'removed', label: 'messages removed', points: data.messagesRemoved, slot: 5 },
                ]}
              />
            </div>
          </Panel>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel title="New members still here">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-left text-muted-foreground">
                  <tr>
                    <th className="py-1 font-medium">After</th>
                    <th className="py-1 text-right font-medium">Joined</th>
                    <th className="py-1 text-right font-medium">Still here</th>
                    <th className="py-1 text-right font-medium">Still active</th>
                  </tr>
                </thead>
                <tbody>
                  {data.newMembers.map((n) => (
                    <tr key={n.days} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                      <td className="py-1">{n.days} days</td>
                      <td className="py-1 text-right tabular-nums">{compactNumber(n.joined)}</td>
                      <td className="py-1 text-right tabular-nums">
                        {compactNumber(n.stillHere)} <span className="text-muted-foreground">{percent(n.stillHere, n.joined)}</span>
                      </td>
                      <td className="py-1 text-right tabular-nums">
                        {compactNumber(n.stillActive)} <span className="text-muted-foreground">{percent(n.stillActive, n.joined)}</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Panel>

            <Panel title="Member health">
              <div className="mb-3 grid grid-cols-3 gap-3">
                <Stat label="Members" value={compactNumber(data.health.members)} />
                <Stat
                  label="Active in 30 days"
                  value={percent(data.health.activeLast30Days, data.health.members)}
                />
                <Stat label="Went quiet" value={compactNumber(data.health.wentQuiet)} />
              </div>
              {data.health.quiet.length === 0 ? (
                <Nothing height={60}>Nobody went quiet.</Nothing>
              ) : (
                <PeopleTable people={data.health.quiet} />
              )}
            </Panel>
          </div>

          <Panel title="Top contributors">
            {data.topContributors.length === 0 ? (
              <Nothing>No messages in this range.</Nothing>
            ) : (
              <PeopleTable people={data.topContributors} />
            )}
          </Panel>

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </>
      )}
    </div>
  )
}

/** Everybody here is a Discord member, so every name opens the Discord person popup. */
function PeopleTable({ people }: { people: ServerContributor[] }) {
  return (
    <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
      <thead className="text-left text-muted-foreground">
        <tr>
          <th className="py-1 font-medium">Person</th>
          <th className="py-1 text-right font-medium">Messages</th>
          <th className="py-1 text-right font-medium">Voice</th>
        </tr>
      </thead>
      <tbody>
        {people.map((p) => (
          <tr key={p.who.id} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
            <td className="py-1">
              <PersonLink platform={p.who.platform} id={p.who.id} name={p.who.name} />
            </td>
            <td className="py-1 text-right tabular-nums">{compactNumber(p.messages)}</td>
            <td className="py-1 text-right tabular-nums text-muted-foreground">
              {p.voiceMinutes > 0 ? minutes(p.voiceMinutes) : '·'}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
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
