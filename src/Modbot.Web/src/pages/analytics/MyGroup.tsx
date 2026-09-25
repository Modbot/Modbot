import { useCallback, useState } from 'react'
import { AlertsCard } from '@/components/alerts/AlertsCard'
import { Badge } from '@/components/ui/badge'
import { DailyBars, DailyLine, Legend, RankedList, compactNumber, dateTime, longDay, percent } from '@/components/charts'
import { api, type MemberCountPeaks } from '@/lib/api'
import { ago } from '@/lib/format'
import { InsightsPanel } from './InsightsPanel'
import { MemberCountChart } from './MemberCountChart'
import { EmptyRow, PanelGrid } from '@/components/PanelGrid'
import { CoverageNote, PageMessage, Panel, RangePicker, Stat, StatStrip } from './shared'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
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
 *
 * The member count chart has a range of its own (`MemberCountChart`): it is drawn from every
 * five-minute reading, not from the page's whole-day window.
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
    <div className="flex flex-col gap-3">
      <AlertsCard />

      <RangePicker range={range} onChange={setRange} from={data?.from} to={data?.to} />

      {!data && <PageMessage>Loading…</PageMessage>}

      {data && (
        // One sheet: every panel on the page shares its edges with the next, rows of two sit in a
        // nested grid, and the six readings run across the top like the gauges on a panel.
        <PanelGrid className="grid-cols-1">
          <StatStrip className="md:grid-cols-3 xl:grid-cols-6">
            <Stat
              label="Members"
              value={latestCount ? compactNumber(latestCount.value) : '—'}
              note={latestCount ? longDay(latestCount.day) : undefined}
            />
            <Stat label="Joined" value={compactNumber(joined)} />
            <Stat label="Left" value={compactNumber(left)} />
            <Stat label="Net change" value={`${joined - left >= 0 ? '+' : ''}${compactNumber(joined - left)}`} />
            <Peaks peaks={data.peaks} />
          </StatStrip>

          <PeaksCoverage peaks={data.peaks} />

          <InsightsPanel />

          <MemberCountChart />

          <PanelGrid className="lg:grid-cols-2">
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
          </PanelGrid>

          <Panel title="Invites and join requests" flush>
            <StatStrip className="m-0">
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
            </StatStrip>
            <div className="p-(--panel-pad)">
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
            </div>
          </Panel>

          <PanelGrid className="lg:grid-cols-2">
            <Panel
              flush
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
                <EmptyRow>No roles yet.</EmptyRow>
              ) : (
                <Table
                  head={
                    <>
                      <Th>Role</Th>
                      <Th className="text-right">Given</Th>
                      <Th className="text-right">Taken away</Th>
                    </>
                  }
                >
                  {data.roles.map((r) => (
                    <Tr key={r.id}>
                      <Td>
                        <span className="font-medium">{r.name ?? r.id}</span>
                        {r.isModerationRole && <Badge variant="secondary" className="ml-2">moderation</Badge>}
                        {r.isAddedOnJoin && <Badge variant="outline" className="ml-2">given on join</Badge>}
                        {r.isSelfAssignable && <Badge variant="outline" className="ml-2">self-service</Badge>}
                      </Td>
                      <Td className="text-right font-mono">{compactNumber(r.granted)}</Td>
                      <Td className="text-right font-mono">{compactNumber(r.revoked)}</Td>
                    </Tr>
                  ))}
                </Table>
              )}
            </Panel>

            <Panel
              title="How long members have been members"
              flush={data.membersWithKnownTenure === 0}
              right={
                data.membersWithKnownTenure > 0 && (
                  <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                    {compactNumber(data.membersWithKnownTenure)} with a known join date
                  </span>
                )
              }
            >
              {data.membersWithKnownTenure === 0 ? (
                <EmptyRow>No data yet.</EmptyRow>
              ) : (
                <RankedList
                  slot={1}
                  rows={data.tenure.map((b) => ({ key: b.label, label: b.label, value: b.members }))}
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

/**
 * The highest the two counts reached in the range, each with the reading that reached it.
 *
 * Both are numbers VRChat reported, taken from the five-minute readings rather than from the daily
 * totals -- a daily total holds the last reading of a day and would put every peak at midnight. The
 * coverage line says how many of the range's days carry a reading at all, because a high water mark
 * across days nobody was reading is the highest Modbot saw and not the highest there was.
 */
function Peaks({ peaks }: { peaks: MemberCountPeaks }) {
  return (
    <>
      <Stat
        label="Most members"
        value={peaks.members ? compactNumber(peaks.members.value) : '—'}
        note={peaks.members ? dateTime(peaks.members.at) : undefined}
      />
      <Stat
        label="Most online at once"
        value={peaks.online ? compactNumber(peaks.online.value) : '—'}
        note={peaks.online ? dateTime(peaks.online.at) : undefined}
      />
    </>
  )
}

/** How many of the range's days the peaks above were read from, when that is not all of them. */
function PeaksCoverage({ peaks }: { peaks: MemberCountPeaks }) {
  const { coverage } = peaks

  if (coverage.readings === 0) return <PageMessage>No member count readings in this range.</PageMessage>

  if (coverage.thin) {
    return (
      <PageMessage>
        Readings on {coverage.daysWithReadings} of {coverage.windowDays} days in this range.
      </PageMessage>
    )
  }

  return null
}
