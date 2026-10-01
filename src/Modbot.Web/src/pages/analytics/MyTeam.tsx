import { useState } from 'react'
import {
  DailyBars,
  DailyLine,
  Heatmap,
  RankedList,
  chartTheme,
  compactNumber,
  dateTime,
  longDay,
  minutes,
  percent,
} from '@/components/charts'
import { InstanceLink } from '@/components/facts'
import type { ActionGroup, CoverageGap, CoverWeek, ModeratorSummary, QueueName, TeamAnalytics, TeamMiddle } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Panel, Stat, StatStrip, Toggle } from './shared'
import { NarrowChevron, NarrowDetails, NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { plural } from '@/lib/format'
import { countsByKind } from '@/lib/rowFacts'

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const HOURS = Array.from({ length: 24 }, (_, h) => `${h}:00`)

/** The people bars offered, before the saved one if it is none of these. */
const PEOPLE_BARS = [1, 3, 5, 10]

const QUEUES: { value: QueueName; label: string }[] = [
  { value: 'join-requests', label: 'Join requests' },
  { value: 'flags', label: 'Flags' },
  { value: 'reviews', label: 'Reviews' },
]

/**
 * The team on the Stats page's Moderation tab: is moderation keeping up, and is the team OK?
 * (spec 10.1, 5.8, Stats page design, analytics design review F7-F9). It was the Team page until the
 * charts of every platform moved onto one Stats page; `/analytics/team` opens that tab.
 *
 * Actions on people and door work are two numbers, never one, so a moderator who runs the door
 * does not read as one who runs people out of it. The reader's own row comes first, beside their
 * usual and the team's middle, and nobody else's numbers are shown to somebody who cannot read the
 * audit log, which is where who-did-what already lives: a page that ranks volunteers burns them out.
 *
 * Coverage gaps come from the fact log, because they are about minutes rather than days; the week
 * grid lays them over the hours they fell in, so "Friday evenings keep going uncovered" can be seen
 * rather than added up from rows. The people bar is the group's saved one, and choosing another
 * saves it for somebody who may change settings.
 */
export function TeamStats({
  data,
  onOpenSubject,
  onOpenReviews,
  onPeople,
}: {
  data: TeamAnalytics
  onOpenSubject?: (subjectId: string) => void
  /** Opens the Reviews page. Passed only when the signed-in person may review. */
  onOpenReviews?: () => void
  /** Chooses another people bar: read again with it, and saved when the reader may change settings. */
  onPeople?: (people: number) => void
}) {
  const [openGaps, toggleGap] = useOpenRows()
  const [openModerators, toggleModerator] = useOpenRows()
  const [perDay, setPerDay] = useState<ActionGroup>('people')

  const people = data.cover.people
  const shownGaps = [...data.coverageGaps.filter((g) => g.peopleWhenLastModeratorLeft >= people)].reverse()
  const onPeopleTotal = data.onPeoplePerDay.reduce((s, p) => s + p.value, 0)
  const doorTotal = data.doorAndAdminPerDay.reduce((s, p) => s + p.value, 0)
  const gapKey = (g: CoverageGap) => `${g.worldId}:${g.instanceId}:${g.startedAt}`
  const moderatorKey = (m: ModeratorSummary) => `${m.who.platform}:${m.who.id}`
  const named = data.canSeeEachModerator

  const peopleBars = [...new Set([...PEOPLE_BARS, people])].sort((a, b) => a - b)
  const reviews = onOpenReviews && (
    <Button variant="outline" size="xs" onClick={onOpenReviews}>
      Reviews of unusual patterns →
    </Button>
  )

  return (
    <PanelGrid className="grid-cols-1">
      {data.you && <You you={data.you} middle={data.middle} />}

      <StatStrip className="xl:grid-cols-5" phonePairs>
        <Stat label="Moderators active" value={compactNumber(data.moderatorsActive)} />
        <Stat label="Actions on people" value={compactNumber(onPeopleTotal)} />
        <Stat label="Door and admin" value={compactNumber(doorTotal)} />
        <Stat label="Left without a moderator" value={compactNumber(data.coverageGaps.length)} />
        <Stat
          label="Instances with no moderator in them"
          value={compactNumber(data.instancesOpenedWithoutAnyWatch)}
          note={
            <>
              of <span className="font-mono">{compactNumber(data.instancesOpenedWithoutAnyWatch + data.instancesWatched)}</span>{' '}
              opened
            </>
          }
        />
      </StatStrip>

      <Panel
        title="Left without a moderator"
        flush
        right={
          onPeople && (
            <Toggle
              value={String(people)}
              onChange={(v) => onPeople(Number(v))}
              options={peopleBars.map((n, i) => ({ value: String(n), label: i === 0 ? `${n}+ people` : `${n}+` }))}
            />
          )
        }
      >
        <div className="border-b border-(length:--hairline) p-(--panel-pad)">
          <CoverGrid cover={data.cover} />
        </div>

        {data.coverageGaps.length === 0 ? (
          <EmptyRow>
            {data.instancesWatched === 0 ? 'No presence reports in this range.' : 'No gaps in this range.'}
          </EmptyRow>
        ) : shownGaps.length === 0 ? (
          <EmptyRow>No gaps with that many people.</EmptyRow>
        ) : (
          <Table
            pinFirst
            narrow={
              <NarrowRows>
                {shownGaps.map((g) => (
                  <NarrowGap
                    key={gapKey(g)}
                    gap={g}
                    named={named}
                    open={openGaps.has(gapKey(g))}
                    onToggle={() => toggleGap(gapKey(g))}
                    onOpenSubject={onOpenSubject}
                  />
                ))}
              </NarrowRows>
            }
            head={
              <>
                <Th>Began</Th>
                <Th>Lasted</Th>
                <Th className="text-right">People left behind</Th>
                {named && <Th>Last moderator out</Th>}
                <Th>Ended because</Th>
                <Th>Instance</Th>
              </>
            }
          >
            {shownGaps.map((g) => (
              <GapRow key={gapKey(g)} gap={g} named={named} onOpenSubject={onOpenSubject} />
            ))}
          </Table>
        )}
      </Panel>

      <Waits data={data} />

      <PanelGrid className="lg:grid-cols-2">
        <Panel title="Acted on again">
          <StatStrip className="xl:grid-cols-2">
            <Stat label="People acted on" value={compactNumber(data.actedOnAgain.people)} />
            <Stat
              label={`Acted on again within ${data.actedOnAgain.days} days`}
              value={compactNumber(data.actedOnAgain.again)}
              note={data.actedOnAgain.people > 0 ? percent(data.actedOnAgain.again, data.actedOnAgain.people) : undefined}
            />
          </StatStrip>
        </Panel>

        <BansLiftedPanel data={data} />
      </PanelGrid>

      {named && (
        <Panel title="Actions per moderator" flush right={reviews}>
          {data.moderators.length === 0 ? (
            <EmptyRow>No moderation actions recorded in this range.</EmptyRow>
          ) : (
            <Table
              pinFirst
              narrow={
                <NarrowRows>
                  {data.moderators.map((m) => {
                    const open = openModerators.has(moderatorKey(m))
                    return (
                      <NarrowRow
                        key={moderatorKey(m)}
                        main={
                          <span className="block truncate">
                            <ModeratorName who={m.who} onOpenSubject={onOpenSubject} />
                          </span>
                        }
                        side={
                          <>
                            <span className="font-mono font-medium">{compactNumber(m.onPeople)}</span>
                            <NarrowChevron open={open} />
                          </>
                        }
                        facts={[
                          <span key="door">
                            door and admin <span className="font-mono">{compactNumber(m.doorAndAdmin)}</span>
                          </span>,
                          m.lastActiveDay && (
                            <span key="active">
                              active <span className="font-mono">{longDay(m.lastActiveDay)}</span>
                            </span>
                          ),
                        ]}
                        onOpen={() => toggleModerator(moderatorKey(m))}
                        open={open}
                        hasLinks={!!onOpenSubject}
                      >
                        {open && (
                          <NarrowDetails
                            items={[
                              { label: 'Days active', value: <span className="font-mono">{m.daysActive}</span> },
                              ...countsByKind(data.kinds, m.byKind).map((c) => ({
                                label: c.label,
                                value: <span className="font-mono">{c.count ? compactNumber(c.count) : '—'}</span>,
                              })),
                            ]}
                          />
                        )}
                      </NarrowRow>
                    )
                  })}
                </NarrowRows>
              }
              head={
                <>
                  <Th>Moderator</Th>
                  <Th className="text-right">Actions on people</Th>
                  <Th className="text-right">Door and admin</Th>
                  <Th className="text-right">Days active</Th>
                  {data.kinds.map((k) => (
                    <Th key={k.metric} className="text-right">
                      {k.label}
                    </Th>
                  ))}
                  <Th>Last active</Th>
                </>
              }
            >
              {data.moderators.map((m) => (
                <Tr key={moderatorKey(m)}>
                  <Td className="whitespace-nowrap">
                    <ModeratorName who={m.who} onOpenSubject={onOpenSubject} />
                  </Td>
                  <Td className="text-right font-mono font-medium">{compactNumber(m.onPeople)}</Td>
                  <Td className="text-right font-mono font-medium">{compactNumber(m.doorAndAdmin)}</Td>
                  <Td className="text-right font-mono text-muted-foreground">{m.daysActive}</Td>
                  {data.kinds.map((k) => (
                    <Td key={k.metric} className="text-right font-mono text-muted-foreground">
                      {m.byKind[k.metric] ? compactNumber(m.byKind[k.metric]) : '—'}
                    </Td>
                  ))}
                  <Td className="font-mono text-muted-foreground">{m.lastActiveDay ? longDay(m.lastActiveDay) : '—'}</Td>
                </Tr>
              ))}
            </Table>
          )}
        </Panel>
      )}

      <PanelGrid className="lg:grid-cols-2">
        <Panel
          title="Actions per day"
          right={
            <span className="flex flex-wrap items-center justify-end gap-2">
              {!named && reviews}
              <Toggle
                value={perDay}
                onChange={setPerDay}
                options={[
                  { value: 'people', label: 'On people' },
                  { value: 'door', label: 'Door and admin' },
                ]}
              />
            </span>
          }
        >
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutAuditLog}
            today={data.today}
            series={[
              perDay === 'people'
                ? { key: 'people', label: 'actions on people', one: 'action on people', points: data.onPeoplePerDay, slot: 1 }
                : { key: 'door', label: 'door and admin', points: data.doorAndAdminPerDay, slot: 2 },
            ]}
          />
        </Panel>

        <Panel title="What kind of actions" flush={onPeopleTotal + doorTotal === 0}>
          {onPeopleTotal + doorTotal === 0 ? (
            <EmptyRow>Nothing yet.</EmptyRow>
          ) : (
            <RankedList
              slot={1}
              rows={data.actionsPerDayByKind
                .filter((k) => k.total > 0)
                .sort((a, b) => b.total - a.total)
                .map((k) => ({ key: k.metric, label: k.label, value: k.total }))}
            />
          )}
        </Panel>
      </PanelGrid>
    </PanelGrid>
  )
}

/**
 * The reader's own numbers, first on the tab: what they did, beside the team's middle and their own
 * usual. Never a rank -- the question a moderator brings here is "am I doing about what I usually
 * do", not "where am I in the table".
 */
function You({ you, middle }: { you: ModeratorSummary; middle: TeamMiddle | null }) {
  const usual = [
    you.usualPerDay !== null && `your usual ${you.usualPerDay}`,
    middle?.onPeoplePerDay != null && `team middle ${middle.onPeoplePerDay}`,
  ].filter(Boolean)

  return (
    <Panel title={you.who.name ? `You · ${you.who.name}` : 'You'}>
      <StatStrip phonePairs>
        <Stat
          label="Actions on people"
          value={compactNumber(you.onPeople)}
          note={middle && `team middle ${compactNumber(middle.onPeople)}`}
        />
        <Stat
          label="Door and admin"
          value={compactNumber(you.doorAndAdmin)}
          note={middle && `team middle ${compactNumber(middle.doorAndAdmin)}`}
        />
        <Stat
          label="Days active"
          value={compactNumber(you.daysActive)}
          note={middle && `team middle ${compactNumber(middle.daysActive)}`}
        />
        <Stat
          label="Actions on people per active day"
          value={you.onPeoplePerDay ?? '—'}
          note={usual.length > 0 ? usual.join(' · ') : undefined}
        />
      </StatStrip>
    </Panel>
  )
}

/**
 * The busy hours of the week, shaded by how many of them had nobody on, in the viewer's own time.
 * Hours Modbot could not see into are striped rather than left blank, so "not known" never reads
 * as "covered".
 */
function CoverGrid({ cover }: { cover: CoverWeek }) {
  const busy = toLocalWeek(cover.busy)
  const nobodyOn = toLocalWeek(cover.nobodyOn)
  const notSeen = toLocalWeek(cover.notSeen)

  if (cover.busy.every((v) => v === 0))
    return <p className="text-muted-foreground">No busy hours in this range.</p>

  return (
    <Heatmap
      rows={DAYS}
      cols={HOURS}
      values={nobodyOn}
      valueLabel="hours with nobody on"
      color={chartTheme.warn}
      hatched={notSeen.map((row, r) => row.map((v, c) => v > 0 && nobodyOn[r][c] === 0))}
      describe={(r, c) => (
        <>
          {DAYS[r]} {HOURS[c]} ·{' '}
          <span className="font-mono font-medium text-foreground">{nobodyOn[r][c]}</span> of{' '}
          <span className="font-mono">{busy[r][c]}</span> busy {plural(busy[r][c], 'hour')} with nobody on
          {notSeen[r][c] > 0 && (
            <>
              {' '}
              · <span className="font-mono">{notSeen[r][c]}</span> not seen
            </>
          )}
        </>
      )}
    />
  )
}

/**
 * 168 hour-of-week buckets, Monday 00:00 UTC first, moved to the viewer's clock and cut into days,
 * as the Activity tab's heatmap does it.
 */
function toLocalWeek(buckets: number[]): number[][] {
  const shift = Math.round(-new Date().getTimezoneOffset() / 60)
  const grid = DAYS.map(() => new Array<number>(24).fill(0))

  buckets.forEach((value, utcIndex) => {
    const local = (((utcIndex + shift) % 168) + 168) % 168
    grid[Math.floor(local / 24)][local % 24] += value
  })

  return grid
}

/**
 * How long join requests, flags and reviews waited for somebody to decide them: the middle wait of
 * each, and the chosen one's middle by day.
 */
function Waits({ data }: { data: TeamAnalytics }) {
  const [queue, setQueue] = useState<QueueName>('join-requests')
  const chosen = data.waits.find((w) => w.queue === queue)
  const label = (q: QueueName) => QUEUES.find((x) => x.value === q)?.label ?? q

  return (
    <Panel title="How long things wait" right={<Toggle value={queue} onChange={setQueue} options={QUEUES} />}>
      <div className="flex flex-col gap-3">
        <StatStrip className="xl:grid-cols-3">
          {data.waits.map((w) => (
            <Stat
              key={w.queue}
              label={label(w.queue)}
              value={w.middleMinutes === null ? '—' : minutes(w.middleMinutes)}
              note={`${compactNumber(w.decided)} decided`}
            />
          ))}
        </StatStrip>

        <DailyLine
          from={data.from}
          to={data.to}
          mode="gap"
          today={data.today}
          format={minutes}
          emptyText="Nothing decided in this range."
          series={[{ key: queue, label: label(queue).toLowerCase(), points: chosen?.middleMinutesPerDay ?? [], slot: 1 }]}
        />
      </div>
    </Panel>
  )
}

function BansLiftedPanel({ data }: { data: TeamAnalytics }) {
  const lifted = data.bansLifted
  const reasons = [
    ...lifted.reasons.map((r) => ({ key: `reason:${r.label}`, label: r.label, value: r.count })),
    ...(lifted.liftedWithoutReason > 0
      ? [{ key: 'none', label: 'No reason given', value: lifted.liftedWithoutReason }]
      : []),
  ]

  return (
    <Panel title="Bans lifted">
      <div className="flex flex-col gap-3">
        <StatStrip className="xl:grid-cols-2">
          <Stat label="Bans" value={compactNumber(lifted.bans)} />
          <Stat
            label={`Lifted within ${lifted.days} days`}
            value={compactNumber(lifted.liftedWithin)}
            note={lifted.bans > 0 ? percent(lifted.liftedWithin, lifted.bans) : undefined}
          />
        </StatStrip>
        {reasons.length > 0 && <RankedList slot={2} rows={reasons} />}
      </div>
    </Panel>
  )
}

/**
 * Which rows of a phone list are open, by key. Several can be open at once, so two moderators'
 * counts can be read one above the other.
 */
function useOpenRows() {
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set())
  const toggle = (key: string) =>
    setOpen((was) => {
      const next = new Set(was)
      if (!next.delete(key)) next.add(key)
      return next
    })
  return [open, toggle] as const
}

function ModeratorName({
  who,
  onOpenSubject,
}: {
  who: { id: string; name: string | null }
  onOpenSubject?: (subjectId: string) => void
}) {
  const name = who.name ?? who.id
  return onOpenSubject ? (
    <button type="button" className="font-medium hover:underline" onClick={() => onOpenSubject(who.id)}>
      {name}
    </button>
  ) : (
    <span className="font-medium">{name}</span>
  )
}

function gapLasted(gap: CoverageGap): string | null {
  return gap.endedAt ? minutes((Date.parse(gap.endedAt) - Date.parse(gap.startedAt)) / 60_000) : null
}

function gapEndedBecause(gap: CoverageGap): string {
  return gap.endedBy === 'moderator-arrived'
    ? 'a moderator arrived'
    : gap.endedBy === 'instance-closed'
      ? 'the instance was closed'
      : 'nothing more was reported'
}

function LastModeratorOut({ gap, onOpenSubject }: { gap: CoverageGap; onOpenSubject?: (id: string) => void }) {
  if (!gap.lastModerator) return <span className="text-muted-foreground">a companion stopped reporting</span>
  const { id, name } = gap.lastModerator
  return onOpenSubject ? (
    <button type="button" className="hover:underline" onClick={() => onOpenSubject(id)}>
      {name ?? id}
    </button>
  ) : (
    <>{name ?? id}</>
  )
}

/* Instance ids are user-controlled text (spec 5.3): rendered as text, never as markup. The same link
   every other screen uses, so it opens the instance when Modbot has a row for it and the world when
   it does not. A world Modbot has not read yet is named by its id. */
function GapInstance({ gap }: { gap: CoverageGap }) {
  return (
    <InstanceLink
      modbotInstanceId={gap.modbotInstanceId}
      worldId={gap.worldId}
      worldName={gap.worldName}
      number={gap.instanceId}
      name={gap.instanceName}
    />
  )
}

/** One gap. Who left last is a column only for a reader who may see each moderator. */
function GapRow({
  gap,
  named,
  onOpenSubject,
}: {
  gap: CoverageGap
  named: boolean
  onOpenSubject?: (id: string) => void
}) {
  return (
    <Tr>
      <Td className="font-mono whitespace-nowrap">{dateTime(gap.startedAt)}</Td>
      <Td className="font-mono whitespace-nowrap">{gapLasted(gap) ?? 'unknown'}</Td>
      <Td className="text-right font-mono font-medium">{gap.peopleWhenLastModeratorLeft}</Td>
      {named && (
        <Td className="min-w-[12rem] whitespace-normal">
          <LastModeratorOut gap={gap} onOpenSubject={onOpenSubject} />
        </Td>
      )}
      <Td className="min-w-[12rem] whitespace-normal text-muted-foreground">{gapEndedBecause(gap)}</Td>
      <Td>
        <span className="inline-block max-w-56 truncate align-bottom">
          <GapInstance gap={gap} />
        </span>
      </Td>
    </Tr>
  )
}

/**
 * A gap on a phone: the instance and how many were left in it, then when and for how long. Who
 * left last and why it ended are words too long to share that second line with a time, so a tap
 * opens them under the row.
 */
function NarrowGap({
  gap,
  named,
  open,
  onToggle,
  onOpenSubject,
}: {
  gap: CoverageGap
  named: boolean
  open: boolean
  onToggle: () => void
  onOpenSubject?: (id: string) => void
}) {
  const lasted = gapLasted(gap)
  const people = gap.peopleWhenLastModeratorLeft

  return (
    <NarrowRow
      main={
        <span className="block truncate">
          <GapInstance gap={gap} />
        </span>
      }
      side={
        <>
          <span className="whitespace-nowrap">
            <span className="font-mono font-medium">{people}</span> {plural(people, 'person', 'people')}
          </span>
          <NarrowChevron open={open} />
        </>
      }
      facts={[
        <span key="began" className="font-mono">
          {dateTime(gap.startedAt)}
        </span>,
        lasted && (
          <span key="lasted">
            lasted <span className="font-mono">{lasted}</span>
          </span>
        ),
      ]}
      onOpen={onToggle}
      open={open}
      hasLinks
    >
      {open && (
        <NarrowDetails
          items={[
            ...(named
              ? [{ label: 'Last moderator out', value: <LastModeratorOut gap={gap} onOpenSubject={onOpenSubject} /> }]
              : []),
            { label: 'Ended because', value: gapEndedBecause(gap) },
          ]}
        />
      )}
    </NarrowRow>
  )
}
