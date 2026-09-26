import { useCallback, useState } from 'react'
import { DailyBars, RankedList, compactNumber, dateTime, longDay, minutes } from '@/components/charts'
import { InstanceLink } from '@/components/facts'
import { api, type CoverageGap } from '@/lib/api'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { Button } from '@/components/ui/button'
import { CoverageNote, PageMessage, Panel, RangePicker, Stat, StatStrip, Toggle } from './shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { useAnalytics, type Range } from './useAnalytics'

/**
 * My Team -- who is doing the moderation work, and when is nobody covering? (spec 10.1, 5.8)
 *
 * The per-moderator numbers come from the daily totals, one metric per kind of action. Coverage
 * gaps come from the fact log, because they are about minutes rather than days, and they are the
 * one figure on any analytics page that suggests an action rather than describing a state.
 */
export function MyTeam({
  onOpenSubject,
  onOpenReviews,
}: {
  onOpenSubject?: (subjectId: string) => void
  /** Opens the Reviews page. Passed only when the signed-in person may review. */
  onOpenReviews?: () => void
}) {
  const [range, setRange] = useState<Range>(30)
  const [minPeople, setMinPeople] = useState<'1' | '3' | '5' | '10'>('3')
  const load = useCallback((q: string) => api.teamAnalytics(q), [])
  const { data, error } = useAnalytics(load, range)

  if (error) return <PageMessage tone="danger">{error}</PageMessage>

  const shownGaps = (data?.coverageGaps ?? []).filter((g) => g.peopleWhenLastModeratorLeft >= Number(minPeople))
  const totalActions = data ? data.actionsPerDay.reduce((s, p) => s + p.value, 0) : 0
  const name = (p: { id: string; name: string | null }) => p.name ?? p.id

  return (
    <div className="flex flex-col gap-3">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <PanelGrid className="grid-cols-1">
          <StatStrip>
            <Stat label="Moderators active" value={compactNumber(data.moderators.length)} />
            <Stat label="Actions" value={compactNumber(totalActions)} />
            <Stat label="Coverage gaps" value={compactNumber(data.coverageGaps.length)} />
            <Stat
              label="Instances nobody watched"
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
            title="Coverage gaps"
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
                {[...shownGaps].reverse().map((g) => (
                  <GapRow key={`${g.worldId}:${g.instanceId}:${g.startedAt}`} gap={g} onOpenSubject={onOpenSubject} />
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
                  <Tr key={`${m.who.platform}:${m.who.id}`}>
                    <Td className="whitespace-nowrap">
                      {onOpenSubject ? (
                        <button type="button" className="font-medium hover:underline" onClick={() => onOpenSubject(m.who.id)}>
                          {name(m.who)}
                        </button>
                      ) : (
                        <span className="font-medium">{name(m.who)}</span>
                      )}
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

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </PanelGrid>
      )}
    </div>
  )
}

function GapRow({ gap, onOpenSubject }: { gap: CoverageGap; onOpenSubject?: (id: string) => void }) {
  const lasted = gap.endedAt
    ? minutes((Date.parse(gap.endedAt) - Date.parse(gap.startedAt)) / 60_000)
    : 'unknown'

  const endedBecause =
    gap.endedBy === 'moderator-arrived'
      ? 'a moderator arrived'
      : gap.endedBy === 'instance-closed'
        ? 'the instance was closed'
        : 'nothing more was reported'

  return (
    <Tr>
      <Td className="font-mono whitespace-nowrap">{dateTime(gap.startedAt)}</Td>
      <Td className="font-mono whitespace-nowrap">{lasted}</Td>
      <Td className="text-right font-mono font-medium">{gap.peopleWhenLastModeratorLeft}</Td>
      <Td className="min-w-[12rem] whitespace-normal">
        {gap.lastModerator ? (
          onOpenSubject ? (
            <button type="button" className="hover:underline" onClick={() => onOpenSubject(gap.lastModerator!.id)}>
              {gap.lastModerator.name ?? gap.lastModerator.id}
            </button>
          ) : (
            gap.lastModerator.name ?? gap.lastModerator.id
          )
        ) : (
          <span className="text-muted-foreground">a companion stopped reporting</span>
        )}
      </Td>
      <Td className="min-w-[12rem] whitespace-normal text-muted-foreground">{endedBecause}</Td>
      {/* Instance ids are user-controlled text (spec 5.3): rendered as text, never as markup. The
          same link every other screen uses, so it opens the instance when Modbot has a row for it
          and the world when it does not. A world Modbot has not read yet is named by its id. */}
      <Td>
        <span className="inline-block max-w-56 truncate align-bottom">
          <InstanceLink
            modbotInstanceId={gap.modbotInstanceId}
            worldId={gap.worldId}
            worldName={gap.worldName}
            number={gap.instanceId}
            name={gap.instanceName}
          />
        </span>
      </Td>
    </Tr>
  )
}
