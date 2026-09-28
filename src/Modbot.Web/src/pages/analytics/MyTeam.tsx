import { useState } from 'react'
import { DailyBars, RankedList, compactNumber, dateTime, longDay, minutes } from '@/components/charts'
import { InstanceLink } from '@/components/facts'
import type { CoverageGap, TeamAnalytics } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { Panel, Stat, StatStrip, Toggle } from './shared'
import { NarrowChevron, NarrowDetails, NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { plural } from '@/lib/format'
import { countsByKind } from '@/lib/rowFacts'

/**
 * The team on the Stats page's Moderation tab: who is doing the moderation work, and when is
 * nobody covering? (spec 10.1, 5.8, Stats page design). It was the Team page until the charts of
 * every platform moved onto one Stats page; `/analytics/team` opens that tab.
 *
 * The per-moderator numbers come from the daily totals, one metric per kind of action. Coverage
 * gaps come from the fact log, because they are about minutes rather than days, and they are the
 * one figure on any analytics page that suggests an action rather than describing a state.
 */
export function TeamStats({
  data,
  onOpenSubject,
  onOpenReviews,
}: {
  data: TeamAnalytics
  onOpenSubject?: (subjectId: string) => void
  /** Opens the Reviews page. Passed only when the signed-in person may review. */
  onOpenReviews?: () => void
}) {
  const [minPeople, setMinPeople] = useState<'1' | '3' | '5' | '10'>('3')
  const [openGaps, toggleGap] = useOpenRows()
  const [openModerators, toggleModerator] = useOpenRows()

  const shownGaps = [...data.coverageGaps.filter((g) => g.peopleWhenLastModeratorLeft >= Number(minPeople))].reverse()
  const totalActions = data.actionsPerDay.reduce((s, p) => s + p.value, 0)
  const gapKey = (g: CoverageGap) => `${g.worldId}:${g.instanceId}:${g.startedAt}`
  const moderatorKey = (m: TeamAnalytics['moderators'][number]) => `${m.who.platform}:${m.who.id}`

  return (
    <PanelGrid className="grid-cols-1">
      <StatStrip>
        <Stat label="Moderators active" value={compactNumber(data.moderators.length)} />
        <Stat label="Actions" value={compactNumber(totalActions)} />
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
          <Toggle
            value={minPeople}
            onChange={setMinPeople}
            options={[
              { value: '1', label: 'Anyone left behind' },
              { value: '3', label: '3+ people' },
              { value: '5', label: '5+' },
              { value: '10', label: '10+' },
            ]}
          />
        }
      >
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
                <Th>Last moderator out</Th>
                <Th>Ended because</Th>
                <Th>Instance</Th>
              </>
            }
          >
            {shownGaps.map((g) => (
              <GapRow key={gapKey(g)} gap={g} onOpenSubject={onOpenSubject} />
            ))}
          </Table>
        )}
      </Panel>

      <Panel
        title="Actions per moderator"
        flush
        right={
          onOpenReviews && (
            <Button variant="outline" size="xs" onClick={onOpenReviews}>
              Reviews of unusual patterns →
            </Button>
          )
        }
      >
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
                          <span className="font-mono font-medium">{compactNumber(m.total)}</span>
                          <NarrowChevron open={open} />
                        </>
                      }
                      facts={[
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
                          items={countsByKind(data.kinds, m.byKind).map((c) => ({
                            label: c.label,
                            value: (
                              <span className="font-mono">{c.count ? compactNumber(c.count) : '—'}</span>
                            ),
                          }))}
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
                <Th className="text-right">Total</Th>
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
                <Td className="text-right font-mono font-medium">{compactNumber(m.total)}</Td>
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

      <PanelGrid className="lg:grid-cols-2">
        <Panel title="Actions per day">
          <DailyBars
            from={data.from}
            to={data.to}
            missing={data.daysWithoutAuditLog}
            today={data.today}
            series={[{ key: 'actions', label: 'actions', one: 'action', points: data.actionsPerDay, slot: 1 }]}
          />
        </Panel>

        <Panel title="What kind of actions" flush={totalActions === 0}>
          {totalActions === 0 ? (
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

function GapRow({ gap, onOpenSubject }: { gap: CoverageGap; onOpenSubject?: (id: string) => void }) {
  return (
    <Tr>
      <Td className="font-mono whitespace-nowrap">{dateTime(gap.startedAt)}</Td>
      <Td className="font-mono whitespace-nowrap">{gapLasted(gap) ?? 'unknown'}</Td>
      <Td className="text-right font-mono font-medium">{gap.peopleWhenLastModeratorLeft}</Td>
      <Td className="min-w-[12rem] whitespace-normal">
        <LastModeratorOut gap={gap} onOpenSubject={onOpenSubject} />
      </Td>
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
  open,
  onToggle,
  onOpenSubject,
}: {
  gap: CoverageGap
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
            { label: 'Last moderator out', value: <LastModeratorOut gap={gap} onOpenSubject={onOpenSubject} /> },
            { label: 'Ended because', value: gapEndedBecause(gap) },
          ]}
        />
      )}
    </NarrowRow>
  )
}
