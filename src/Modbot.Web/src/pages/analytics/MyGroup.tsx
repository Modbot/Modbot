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
              note={latestCount ? `as VRChat reported it on ${longDay(latestCount.day)}` : 'no headcount recorded yet'}
            />
            <Stat label="Joined" value={compactNumber(joined)} note="in this range" />
            <Stat label="Left" value={compactNumber(left)} note="in this range" />
            <Stat
              label="Net change"
              value={`${joined - left >= 0 ? '+' : ''}${compactNumber(joined - left)}`}
              note="joined minus left, in this range"
            />
          </div>

          <Panel
            title="Member count"
            source="From the fact log — the headcount VRChat reported"
            note={
              data.memberCount.length === 0
                ? 'Nothing yet. The group-info sync records the count only when it changes, so this fills in once a change is seen.'
                : 'Held flat between readings, because the sync records a change rather than a daily reading.'
            }
          >
            <DailyLine
              from={data.from}
              to={data.to}
              mode="carry"
              zeroBased={false}
              series={[{ key: 'members', label: 'members', points: data.memberCount, slot: 1 }]}
              emptyText="The headcount appears here once the group-info sync has seen it change."
            />
          </Panel>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel title="Joins and leaves per day" source="From daily totals">
              <Legend items={[{ label: 'Joined', slot: 3 }, { label: 'Left', slot: 2 }]} />
              <div className="mt-2">
                <DailyBars
                  from={data.from}
                  to={data.to}
                  series={[
                    { key: 'joined', label: 'joined', points: data.joined, slot: 3 },
                    { key: 'left', label: 'left', points: data.left, slot: 2 },
                  ]}
                  emptyText="Joins and leaves appear here as the audit log records them. Nothing in this range yet."
                />
              </div>
            </Panel>

            <Panel
              title="Joined minus left, running"
              source="From daily totals"
              note="Recorded joins minus recorded leaves, counted from zero on the first day of the fact log. Not the member count — that is the headcount above."
            >
              <DailyLine
                from={data.from}
                to={data.to}
                mode="carry"
                series={[{ key: 'net', label: 'net', points: data.netChange, slot: 4 }]}
                emptyText="This line starts once the first join or leave is recorded."
              />
            </Panel>
          </div>

          <Panel
            title="The way in"
            source="Invites and requests per day from daily totals; what followed from the fact log"
            note={`An invite counts as accepted when that person joined within ${data.invites.followUpDays} days of it. A request is counted as approved when VRChat records the join as done by a moderator.`}
          >
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
                emptyText="Invites and join requests appear here as the audit log records them."
              />
            </div>
          </Panel>

          <div className="grid gap-4 lg:grid-cols-2">
            <Panel
              title="Roles"
              source={data.rolesKnownAt ? `Role list from VRChat, read ${ago(data.rolesKnownAt, data.generatedAt)}; changes from the fact log` : 'Role list not read yet'}
              note="VRChat does not tell Modbot how many members hold each role — that arrives with the member list sync. What is known is which roles exist and how often each was given or taken away in this range."
            >
              {data.roles.length === 0 ? (
                <Nothing>The role list appears once the group-info sync has read the group.</Nothing>
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
              source="From the fact log, for members whose join was recorded"
              note={
                data.membersWithKnownTenure === 0
                  ? 'Nobody yet. Only people who joined after Modbot started recording have a join date; members from before that are not counted here.'
                  : `${compactNumber(data.membersWithKnownTenure)} current members have a recorded join. Members from before Modbot started recording are not counted, because their join date is not known.`
              }
            >
              {data.membersWithKnownTenure === 0 ? (
                <Nothing>This fills in as people join and stay.</Nothing>
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
