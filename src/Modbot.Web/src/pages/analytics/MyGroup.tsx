import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { DailyBars, DailyLine, Legend, RankedList, compactNumber, longDay, percent } from '@/components/charts'
import { api } from '@/lib/api'
import { ago } from '@/lib/format'
import { CoverageNote, Nothing, PageMessage, Panel, RangePicker, Stat } from './shared'
import { useAnalytics, type Range } from './useAnalytics'

/**
 * My Group -- is the community growing or shrinking, and what changed? (spec 10.1)
 *
 * Two sources on one page, kept visibly apart. Joins, leaves, invites and requests per day come
 * from the daily totals, which are never aged out; the headcount, roles, tenure and invite
 * follow-up come from the fact log, which a retention window can shorten. Each panel says which.
 *
 * `netChange` is deliberately labelled as recorded joins minus recorded leaves and never as the
 * member count: it starts from zero on the fact log's first day, so a group that installed Modbot
 * with 40,000 members would watch it climb from nothing. The headcount is what VRChat reported.
 */
export function MyGroup() {
  const [range, setRange] = useState<Range>(30)
  const load = useCallback((q: string) => api.groupAnalytics(q), [])
  const { data, error } = useAnalytics(load, range)

  if (error) return <PageMessage>{error}</PageMessage>

  const sum = (points: { value: number }[]) => points.reduce((s, p) => s + p.value, 0)
  const latestCount = data?.memberCount[data.memberCount.length - 1]
  const joined = data ? sum(data.joined) : 0
  const left = data ? sum(data.left) : 0

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
            <Stat label="Joined" value={compactNumber(joined)} />
            <Stat label="Left" value={compactNumber(left)} />
            <Stat label="Net change" value={`${joined - left >= 0 ? '+' : ''}${compactNumber(joined - left)}`} />
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

            <Panel title="Joined minus left, running">
              <DailyLine
                from={data.from}
                to={data.to}
                mode="carry"
                series={[{ key: 'net', label: 'net', points: data.netChange, slot: 4 }]}
              />
            </Panel>
          </div>

          <Panel title="Invites and join requests">
            <div className="mb-3 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <Stat label="Invites sent" value={compactNumber(data.invites.invitesSent)} />
              <Stat
                label="Invites accepted"
                value={percent(data.invites.joinedAfterInvite, data.invites.invitesSent)}
                note={`${compactNumber(data.invites.joinedAfterInvite)} joined within ${data.invites.followUpDays} days`}
              />
              <Stat label="Join requests" value={compactNumber(data.invites.requestsReceived)} />
              <Stat
                label="Requests decided"
                value={`${compactNumber(data.invites.requestsApproved)} / ${compactNumber(data.invites.requestsRejected)}`}
                note="approved / rejected"
              />
            </div>
            <Legend items={[{ label: 'Invites sent', slot: 1 }, { label: 'Join requests', slot: 5 }]} />
            <div className="mt-2">
              <DailyBars
                from={data.from}
                to={data.to}
                series={[
                  { key: 'invites', label: 'invites sent', points: data.invitesSent, slot: 1 },
                  { key: 'requests', label: 'join requests', points: data.requestsReceived, slot: 5 },
                ]}
              />
            </div>
          </Panel>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel
              title="Roles"
              right={
                data.rolesKnownAt && (
                  <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    read {ago(data.rolesKnownAt, data.generatedAt)}
                  </span>
                )
              }
            >
              {data.roles.length === 0 ? (
                <Nothing>No roles yet.</Nothing>
              ) : (
                <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                  <thead className="text-left text-muted-foreground">
                    <tr>
                      <th className="py-1 font-medium">Role</th>
                      <th className="py-1 text-right font-medium">Given</th>
                      <th className="py-1 text-right font-medium">Taken away</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.roles.map((r) => (
                      <tr key={r.id} className="border-t" style={{ borderTopWidth: 'var(--hairline)' }}>
                        <td className="py-1">
                          <span className="font-medium">{r.name ?? r.id}</span>
                          {r.isModerationRole && <Badge variant="secondary" className="ml-2">moderation</Badge>}
                          {r.isAddedOnJoin && <Badge variant="outline" className="ml-2">given on join</Badge>}
                          {r.isSelfAssignable && <Badge variant="outline" className="ml-2">self-service</Badge>}
                        </td>
                        <td className="py-1 text-right tabular-nums">{compactNumber(r.granted)}</td>
                        <td className="py-1 text-right tabular-nums">{compactNumber(r.revoked)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Panel>

            <Panel
              title="How long members have been members"
              right={
                data.membersWithKnownTenure > 0 && (
                  <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    {compactNumber(data.membersWithKnownTenure)} with a known join date
                  </span>
                )
              }
            >
              {data.membersWithKnownTenure === 0 ? (
                <Nothing>No data yet.</Nothing>
              ) : (
                <RankedList
                  slot={1}
                  rows={data.tenure.map((b) => ({ key: b.label, label: b.label, value: b.members }))}
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
