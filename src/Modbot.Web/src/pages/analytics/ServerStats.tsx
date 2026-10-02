import { useState } from 'react'
import { Hash, Megaphone, MessagesSquare, Podcast, Volume2 } from 'lucide-react'
import {
  DailyBars,
  DailyLine,
  Heatmap,
  Legend,
  RankedList,
  compactNumber,
  minutes,
  percent,
  seriesColor,
  type SeriesSlot,
} from '@/components/charts'
import { PersonLink } from '@/components/facts'
import type { CurrentUser, MembersNow, ServerAnalytics, ServerChannel, ServerContributor, ServerReach } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { mayOpen, type PageId } from '@/lib/nav'
import { can } from '@/lib/permissions'
import { followLink } from '@/lib/router'
import { channelLook, serverTabHref, type ChannelLook } from '@/lib/serverOverview'
import { CoverageLine, Panel, Stat, StatStrip, Toggle } from './shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { lengthOfTime, plural } from '@/lib/format'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const HOURS = Array.from({ length: 24 }, (_, h) => `${h}:00`)

/** How many channels the busiest list shows before "Show all". */
const CHANNELS_SHOWN = 10

/**
 * The Discord server's parts of the Stats page (M5 spec §6, Stats page design), one per tab: who
 * comes and goes and who the members are (Growth), what the people there do (Activity), and what
 * the moderators did (Moderation). They were the Discord page's charts, named as Discord's Server
 * Insights names its parts, until they moved to Stats; the Discord page keeps "This week".
 *
 * The bot reads only the channels it is allowed to, and kicks and removed messages only from the
 * audit log, so the coverage line says how many channels it reads and whether it has the audit
 * log, and the moderation chart says when kicks and removed messages cannot be in it.
 *
 * Members, messages, voice and moderation per day come from the daily totals. "Active" is having
 * sent a message or been in voice, counted as distinct people over a day, a week and thirty days --
 * the server builds those, because distinct people cannot be summed from daily counts. New members
 * who stayed come from the fact log; went quiet and Members now are about now and ignore the range.
 *
 * Every person listed opens the person popup, as names do everywhere else.
 */

type Links = { me: CurrentUser; pathOf: (id: PageId) => string }

/** Where the Discord numbers come from, with how much of the server the bot can read. */
export function ServerCoverage({ data, me, pathOf }: { data: ServerAnalytics } & Links) {
  return (
    <CoverageLine
      coverage={data.coverage}
      generatedAt={data.generatedAt}
      after={data.reach && <ReachNote reach={data.reach} me={me} pathOf={pathOf} />}
    />
  )
}

const sum = (points: { value: number }[]) => points.reduce((s, p) => s + p.value, 0)

/** Who comes and goes, and who the members are now. */
export function ServerGrowth({ data }: { data: ServerAnalytics }) {
  const [view, setView] = useState<'moves' | 'count'>('moves')
  const firstCount = data.memberCount[0]
  const latestCount = data.memberCount[data.memberCount.length - 1]
  const firstWeek = data.newMembers.find((n) => n.days === 7)

  return (
    <PanelGrid className="grid-cols-1">
      <StatStrip className="sm:grid-cols-3 xl:grid-cols-3">
        <Stat
          label="Members"
          value={latestCount ? compactNumber(latestCount.value) : '—'}
          note={latestCount && firstCount ? signed(latestCount.value - firstCount.value) : undefined}
          noteMono
        />
        <Stat label="Joined" value={compactNumber(sum(data.joined))} />
        <Stat
          label="Still here after 7d"
          value={firstWeek ? percent(firstWeek.stillHere, firstWeek.joined) : '—'}
          note={
            firstWeek && firstWeek.joined > 0
              ? `${compactNumber(firstWeek.stillHere)} of ${compactNumber(firstWeek.joined)}`
              : undefined
          }
          noteMono
        />
      </StatStrip>

      <Panel
        title={view === 'moves' ? 'Joins and leaves per day' : 'Member count'}
        right={
          <Toggle
            value={view}
            onChange={setView}
            options={[
              { value: 'moves', label: 'Joins and leaves' },
              { value: 'count', label: 'Member count' },
            ]}
          />
        }
      >
        {view === 'moves' ? (
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutBot}
            today={data.today}
            legend={[{ label: 'Joined', slot: 3 }, { label: 'Left', slot: 2 }]}
            series={[
              { key: 'joined', label: 'joined', points: data.joined, slot: 3 },
              { key: 'left', label: 'left', points: data.left, slot: 2 },
            ]}
          />
        ) : (
          <DailyLine
            from={data.from}
            to={data.to}
            missing={data.daysWithoutBot}
            today={data.today}
            mode="carry"
            series={[{ key: 'members', label: 'members', one: 'member', points: data.memberCount, slot: 1 }]}
          />
        )}
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
              <Td>{lengthOfTime(n.days * 24 * 60)}</Td>
              <Td className="text-right font-mono">{compactNumber(n.joined)}</Td>
              <Td className="text-right font-mono">
                {compactNumber(n.stillHere)}{' '}
                <span className="text-muted-foreground">{percent(n.stillHere, n.joined)}</span>
              </Td>
              <Td className="text-right font-mono">
                {compactNumber(n.stillActive)}{' '}
                <span className="text-muted-foreground">{percent(n.stillActive, n.joined)}</span>
              </Td>
            </Tr>
          ))}
        </Table>
      </Panel>

      <MembersNowPart now={data.membersNow} />
    </PanelGrid>
  )
}

/** What the people in the server do: messages, voice, who is active and where. */
export function ServerActivity({ data }: { data: ServerAnalytics }) {
  const [activeSpan, setActiveSpan] = useState<'daily' | 'weekly' | 'monthly'>('daily')
  const [allChannels, setAllChannels] = useState(false)
  const channels = allChannels ? data.busiestChannels : data.busiestChannels.slice(0, CHANNELS_SHOWN)

  return (
    <PanelGrid className="grid-cols-1">
      <StatStrip className="sm:grid-cols-2 xl:grid-cols-2" phonePairs>
        <Stat label="Messages" value={compactNumber(sum(data.messages))} />
        <Stat label="Time in voice" value={minutes(sum(data.voiceMinutes))} />
      </StatStrip>

      <PanelGrid className="lg:grid-cols-2">
        <Panel title="Messages per day">
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutMessages}
            today={data.today}
            series={[{ key: 'messages', label: 'messages', one: 'message', points: data.messages, slot: 1 }]}
          />
        </Panel>

        <ActivePanel data={data} span={activeSpan} onSpan={setActiveSpan} />
      </PanelGrid>

      <PanelGrid className="lg:grid-cols-2">
        <Panel title="Time in voice per day">
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutBot}
            today={data.today}
            series={[{ key: 'voice', label: 'in voice', points: data.voiceMinutes, slot: 5 }]}
            format={(v) => lengthOfTime(v)}
          />
        </Panel>

        <Panel title="Busiest channels" flush={data.busiestChannels.length === 0}>
          {data.busiestChannels.length === 0 ? (
            <EmptyRow>No messages in this range.</EmptyRow>
          ) : (
            <div className="flex flex-col gap-2">
              <RankedList
                slot={1}
                rows={channels.map((c) => ({
                  key: c.id,
                  label: c.name ?? c.id,
                  icon: <ChannelIcon look={channelLook(c.type)} />,
                  value: c.messages,
                  note: channelNote(c),
                }))}
              />
              {data.busiestChannels.length > CHANNELS_SHOWN && (
                <button
                  type="button"
                  onClick={() => setAllChannels((all) => !all)}
                  className="self-start rounded-sm text-muted-foreground underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-ring phone:min-h-(--control-h)"
                  style={{ fontSize: 'var(--text-small)' }}
                >
                  {allChannels ? 'Show fewer' : 'Show all'}
                </button>
              )}
            </div>
          )}
        </Panel>
      </PanelGrid>

      <Panel title="Busiest hours (your time)" flush={data.hourOfWeek.messages.every((v) => v === 0)}>
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

      <PanelGrid className="lg:grid-cols-2">
        <Panel
          title="Went quiet"
          flush
          right={<span className="font-mono text-muted-foreground">{compactNumber(data.health.wentQuiet)}</span>}
        >
          {data.health.quiet.length === 0 ? <EmptyRow>Nobody went quiet.</EmptyRow> : <PeopleTable people={data.health.quiet} />}
        </Panel>

        <Panel title="Top contributors" flush>
          {data.topContributors.length === 0 ? (
            <EmptyRow>No messages in this range.</EmptyRow>
          ) : (
            <PeopleTable people={data.topContributors} />
          )}
        </Panel>
      </PanelGrid>
    </PanelGrid>
  )
}

/** What the server's moderators did, with a link to the server's own ban list. */
export function ServerModeration({ data, me, pathOf }: { data: ServerAnalytics } & Links) {
  const bans = serverTabHref('bans', pathOf('bans'))

  return (
    <Panel
      title="Moderation actions per day"
      right={
        mayOpen(me, 'bans') ? (
          <a
            href={bans}
            onClick={followLink(bans)}
            className="rounded-sm text-muted-foreground underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-ring"
            style={{ fontSize: 'var(--text-small)' }}
          >
            Bans
          </a>
        ) : undefined
      }
    >
      <div className="flex flex-col gap-2">
        {data.reach && !data.reach.auditLog && (
          <div className="text-warn" style={{ fontSize: 'var(--text-small)' }}>
            No audit log: kicks and removed messages missing.
          </div>
        )}
        <DailyBars
          from={data.from}
          to={data.to}
          missing={data.daysWithoutBot}
          today={data.today}
          stacked
          emptyText={data.reach && !data.reach.auditLog ? 'No bans or timeouts in this range.' : undefined}
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
            {
              key: 'removed',
              label: 'messages removed',
              one: 'message removed',
              points: data.messagesRemoved,
              slot: 5,
            },
          ]}
        />
      </div>
    </Panel>
  )
}

/** "+3", "−2" or "0": a change in the member count over the range. */
function signed(n: number): string {
  if (n > 0) return `+${compactNumber(n)}`
  if (n < 0) return `−${compactNumber(-n)}`
  return '0'
}

/**
 * "Reads 35 of 165 channels · No audit log", after the coverage line. A link to the Discord part of
 * Settings, where each channel's permissions are listed, for someone who may open it.
 */
function ReachNote({ reach, me, pathOf }: { reach: ServerReach } & Links) {
  const missing = reach.channelsRead < reach.channels || !reach.auditLog
  const text = (
    <>
      Reads{' '}
      <span className="font-mono">
        {compactNumber(reach.channelsRead)} of {compactNumber(reach.channels)}
      </span>{' '}
      {plural(reach.channels, 'channel')}
      {!reach.auditLog && ' · No audit log'}
    </>
  )
  const href = `${pathOf('settings')}#discord`

  return (
    <span className={missing ? 'text-warn' : undefined}>
      {mayOpen(me, 'settings') && can(me, 'ManageSettings') ? (
        <a
          href={href}
          onClick={followLink(href)}
          className="rounded-sm underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-ring"
        >
          {text}
        </a>
      ) : (
        text
      )}
    </span>
  )
}

/** The picture Discord draws before a channel's name, in the size of the list's text. */
export function ChannelIcon({ look }: { look: ChannelLook }) {
  const props = { 'aria-hidden': true, className: 'size-[1em] shrink-0' } as const

  switch (look) {
    case 'voice':
      return <Volume2 {...props} />
    case 'stage':
      return <Podcast {...props} />
    case 'forum':
      return <MessagesSquare {...props} />
    case 'announcement':
      return <Megaphone {...props} />
    default:
      return <Hash {...props} />
  }
}

/** The grey words after a busiest channel's count: "Deleted" for a channel gone from Discord, else its category. */
function channelNote(channel: ServerChannel): string | undefined {
  if (channel.removed) return 'Deleted'
  return channel.category ?? undefined
}

function ActivePanel({
  data,
  span,
  onSpan,
}: {
  data: { from: string; to: string; today: string | null; daysWithoutMessages: string[]; active: { day: string; daily: number; weekly: number; monthly: number }[] }
  span: 'daily' | 'weekly' | 'monthly'
  onSpan: (next: 'daily' | 'weekly' | 'monthly') => void
}) {
  const last = data.active[data.active.length - 1]

  return (
    <Panel
      title="Active members"
      flush
      right={
        <Toggle
          value={span}
          onChange={onSpan}
          options={[
            { value: 'daily', label: 'Day' },
            { value: 'weekly', label: '7d' },
            { value: 'monthly', label: '30d' },
          ]}
        />
      }
    >
      <div className="p-(--panel-pad)">
        <DailyLine
          from={data.from}
          to={data.to}
          missing={data.daysWithoutMessages}
          today={data.today}
          series={[
            {
              key: 'active',
              label: 'active',
              points: data.active.map((a) => ({ day: a.day, value: a[span] })),
              slot: 4,
            },
          ]}
        />
      </div>
      {last && (
        <StatStrip className="m-0 grid-cols-3 xl:grid-cols-3">
          <Stat label="Day" value={compactNumber(last.daily)} />
          <Stat label="7d" value={compactNumber(last.weekly)} />
          <Stat label="30d" value={compactNumber(last.monthly)} />
        </StatStrip>
      )}
    </Panel>
  )
}

const TENURE: { key: keyof MembersNow['tenure']; label: string; slot: SeriesSlot }[] = [
  { key: 'underAMonth', label: 'Under 1mth', slot: 1 },
  { key: 'oneToSixMonths', label: '1–6mth', slot: 2 },
  { key: 'sixToTwelveMonths', label: '6–12mth', slot: 3 },
  { key: 'yearOrMore', label: '1y or more', slot: 4 },
]

/**
 * Who the members are now, bots left out: how many linked a VRChat account, how many recent joiners
 * came on a new Discord account, and how long everyone has been here.
 */
function MembersNowPart({ now }: { now: MembersNow }) {
  const total = TENURE.reduce((s, t) => s + now.tenure[t.key], 0)

  return (
    <>
      <StatStrip className="sm:grid-cols-3 xl:grid-cols-3">
        <Stat label="Linked to VRChat" value={compactNumber(now.linked)} note={`of ${compactNumber(now.members)}`} noteMono />
        <Stat
          label="New accounts joining"
          value={percent(now.newAccounts, now.joined)}
          note={
            now.joined > 0
              ? `${compactNumber(now.newAccounts)} of ${compactNumber(now.joined)} · ${compactNumber(now.newAccountsStillHere)} still here`
              : undefined
          }
          noteMono
        />
        <Stat
          label="Here 1y or more"
          value={percent(now.tenure.yearOrMore, now.members)}
          note={`${compactNumber(now.tenure.yearOrMore)} of ${compactNumber(now.members)}`}
          noteMono
        />
      </StatStrip>

      <Panel title="How long members have been here" flush={total === 0}>
        {total === 0 ? (
          <EmptyRow>Nobody listed yet.</EmptyRow>
        ) : (
          <div className="flex flex-col gap-2">
            <div
              role="img"
              aria-label={TENURE.map((t) => `${t.label}: ${now.tenure[t.key]}`).join(', ')}
              className="flex h-3 w-full overflow-hidden rounded-sm bg-secondary"
            >
              {TENURE.map((t) =>
                now.tenure[t.key] > 0 ? (
                  <div
                    key={t.key}
                    className="h-full"
                    style={{ width: `${(now.tenure[t.key] / total) * 100}%`, background: seriesColor(t.slot) }}
                  />
                ) : null,
              )}
            </div>
            <Legend items={TENURE.map((t) => ({ label: t.label, slot: t.slot, value: compactNumber(now.tenure[t.key]) }))} />
          </div>
        )}
      </Panel>
    </>
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
          <Td className="text-right font-mono text-muted-foreground">{p.voiceMinutes > 0 ? minutes(p.voiceMinutes) : '—'}</Td>
        </Tr>
      ))}
    </Table>
  )
}

/** Shifts the 168 UTC buckets into the viewer's clock and lays them out by day, as on the Instances part. */
function toLocalGrid(buckets: number[]): number[][] {
  const shift = Math.round(-new Date().getTimezoneOffset() / 60)
  const grid = DAYS.map(() => new Array<number>(24).fill(0))

  buckets.forEach((value, utcIndex) => {
    const local = (((utcIndex + shift) % 168) + 168) % 168
    grid[Math.floor(local / 24)][local % 24] += value
  })

  return grid
}
