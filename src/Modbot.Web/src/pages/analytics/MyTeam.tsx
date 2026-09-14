import { useCallback, useState } from 'react'
import { DailyBars, RankedList, compactNumber, dateTime, minutes } from '@/components/charts'
import { WorldLink } from '@/components/facts'
import { api, type CoverageGap } from '@/lib/api'
import { CoverageNote, Nothing, PageMessage, Panel, RangePicker, Stat, Toggle } from './shared'
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

  if (error) return <PageMessage>{error}</PageMessage>

  const shownGaps = (data?.coverageGaps ?? []).filter((g) => g.peopleWhenLastModeratorLeft >= Number(minPeople))
  const totalActions = data ? data.actionsPerDay.reduce((s, p) => s + p.value, 0) : 0
  const name = (p: { id: string; name: string | null }) => p.name ?? p.id

  return (
    <div className="flex flex-col gap-4">
      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        <>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Stat label="Moderators active" value={compactNumber(data.moderators.length)} />
            <Stat label="Actions" value={compactNumber(totalActions)} />
            <Stat label="Coverage gaps" value={compactNumber(data.coverageGaps.length)} />
            <Stat
              label="Instances nobody watched"
              value={compactNumber(data.instancesOpenedWithoutAnyWatch)}
              note={`of ${compactNumber(data.instancesOpenedWithoutAnyWatch + data.instancesWatched)} opened`}
            />
          </div>

          <Panel
            title="Coverage gaps"
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
              <Nothing>
                {data.instancesWatched === 0 ? 'No presence reports in this range.' : 'No gaps in this range.'}
              </Nothing>
            ) : shownGaps.length === 0 ? (
              <Nothing>No gaps with that many people.</Nothing>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                  <thead className="text-left text-muted-foreground">
                    <tr>
                      <th className="py-1 pr-3 font-medium">Began</th>
                      <th className="py-1 pr-3 font-medium">Lasted</th>
                      <th className="py-1 pr-3 text-right font-medium">People left behind</th>
                      <th className="py-1 pr-3 font-medium">Last moderator out</th>
                      <th className="py-1 pr-3 font-medium">Ended because</th>
                      <th className="py-1 font-medium">Instance</th>
                    </tr>
                  </thead>
                  <tbody>
                    {[...shownGaps].reverse().map((g) => (
                      <GapRow key={`${g.worldId}:${g.instanceId}:${g.startedAt}`} gap={g} onOpenSubject={onOpenSubject} />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>

          <Panel
            title="Actions per moderator"
            right={
              onOpenReviews && (
                <button
                  type="button"
                  className="rounded-md border px-2 font-medium text-muted-foreground hover:text-foreground"
                  style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)', height: 'calc(var(--control-h) - 8px)' }}
                  onClick={onOpenReviews}
                >
                  Reviews of unusual patterns →
                </button>
              )
            }
          >
            {data.moderators.length === 0 ? (
              <Nothing>No moderation actions recorded in this range.</Nothing>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                  <thead className="text-left text-muted-foreground">
                    <tr>
                      <th className="py-1 pr-3 font-medium">Moderator</th>
                      <th className="py-1 pr-3 text-right font-medium">Total</th>
                      {data.kinds.map((k) => (
                        <th key={k.metric} className="py-1 pr-3 text-right font-medium">
                          {k.label}
                        </th>
                      ))}
                      <th className="py-1 font-medium">Last active</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.moderators.map((m) => (
                      <tr key={`${m.who.platform}:${m.who.id}`} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                        <td className="py-1 pr-3">
                          {onOpenSubject ? (
                            <button type="button" className="font-medium hover:underline" onClick={() => onOpenSubject(m.who.id)}>
                              {name(m.who)}
                            </button>
                          ) : (
                            <span className="font-medium">{name(m.who)}</span>
                          )}
                        </td>
                        <td className="py-1 pr-3 text-right font-medium tabular-nums">{compactNumber(m.total)}</td>
                        {data.kinds.map((k) => (
                          <td key={k.metric} className="py-1 pr-3 text-right tabular-nums text-muted-foreground">
                            {m.byKind[k.metric] ? compactNumber(m.byKind[k.metric]) : '·'}
                          </td>
                        ))}
                        <td className="py-1 text-muted-foreground">{m.lastActiveDay ?? '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Panel>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel title="Actions per day">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[{ key: 'actions', label: 'actions', points: data.actionsPerDay, slot: 1 }]}
              />
            </Panel>

            <Panel title="What kind of actions">
              {totalActions === 0 ? (
                <Nothing>Nothing yet.</Nothing>
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
          </div>

          <CoverageNote coverage={data.coverage} generatedAt={data.generatedAt} />
        </>
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
    <tr className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
      <td className="py-1 pr-3 whitespace-nowrap">{dateTime(gap.startedAt)}</td>
      <td className="py-1 pr-3 whitespace-nowrap">{lasted}</td>
      <td className="py-1 pr-3 text-right font-medium tabular-nums">{gap.peopleWhenLastModeratorLeft}</td>
      <td className="py-1 pr-3">
        {gap.lastModerator ? (
          onOpenSubject ? (
            <button type="button" className="hover:underline" onClick={() => onOpenSubject(gap.lastModerator!.id)}>
              {gap.lastModerator.name ?? gap.lastModerator.id}
            </button>
          ) : (
            gap.lastModerator.name ?? gap.lastModerator.id
          )
        ) : (
          <span className="text-muted-foreground">a client stopped reporting</span>
        )}
      </td>
      <td className="py-1 pr-3 text-muted-foreground">{endedBecause}</td>
      {/* Instance ids are user-controlled text (spec 5.3): rendered as text, never as markup. */}
      <td className="py-1 text-muted-foreground" title={`${gap.worldId}:${gap.instanceId}`}>
        {/* The world opens its popup. The gap carries no world name, so the id is the label rather
            than a "not read yet" nobody checked. */}
        <span className="inline-block max-w-56 truncate align-bottom">
          <WorldLink id={gap.worldId} unnamed="id" /> <span className="font-mono">#{gap.instanceId}</span>
        </span>
      </td>
    </tr>
  )
}
