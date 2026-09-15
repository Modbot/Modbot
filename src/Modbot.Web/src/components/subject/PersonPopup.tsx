import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Tabs } from '@/components/ui/tabs'
import { compactNumber, dateTime, minutes } from '@/components/charts'
import { RoomTable } from '@/components/RoomTable'
import { SubjectCaseFiles } from '@/components/SubjectCaseFiles'
import { SubjectHistory } from '@/components/SubjectHistory'
import { UserProfileCard } from '@/components/UserProfileCard'
import { DiscordLinkCard } from '@/components/subject/DiscordLinkCard'
import { FactList, Figure, Note, Panel, PopupFrame } from '@/components/subject/shared'
import { useLoad } from '@/lib/useLoad'
import { api, type CurrentUser } from '@/lib/api'
import { ago, formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'

type Tab = 'logs' | 'cases' | 'metrics'

/**
 * One person: their VRChat profile on the left, and what Modbot has recorded about them on the
 * right.
 *
 * The profile card, the history counts and the case files are the same components the side pane
 * used, moved rather than rewritten.
 */
export function PersonPopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const [tab, setTab] = useState<Tab>('logs')

  const tabs: { value: Tab; label: string }[] = [
    { value: 'logs', label: 'Logs' },
    ...(can(me, 'ViewProfile') ? [{ value: 'cases' as const, label: 'Cases' }] : []),
    ...(can(me, 'ViewProfile') ? [{ value: 'metrics' as const, label: 'Metrics' }] : []),
  ]

  return (
    <PopupFrame
      title="Person"
      // The id verbatim and unparsed: VRChat ids are opaque, and a legacy one looks nothing like
      // a modern one (spec 3.1.1).
      subtitle={<span className="font-mono" title={id}>{id}</span>}
      lead={lead}
      left={
        <>
          <UserProfileCard subjectId={id} me={me} />
          {can(me, 'ViewProfile') && <DiscordLinkCard subjectId={id} me={me} />}

          {can(me, 'ViewMembers') && <MembershipCard subjectId={id} />}
        </>
      }
    >
      <Tabs value={tab} onChange={setTab} tabs={tabs}>
        {tab === 'logs' && <Logs id={id} />}
        {tab === 'cases' && (
          <div className="p-4">
            <SubjectCaseFiles subjectId={id} />
          </div>
        )}
        {tab === 'metrics' && <Metrics id={id} />}
      </Tabs>
    </PopupFrame>
  )
}

function Logs({ id }: { id: string }) {
  const load = useCallback(() => api.audit({ subject: id, limit: 50 }), [id])
  const { data, error } = useLoad(load)

  return (
    <div className="flex flex-col gap-3 p-4">
      <SubjectHistory subjectId={id} />

      <div className="font-medium">Everything recorded about this person</div>

      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </div>
  )
}

/**
 * What Modbot can actually work out about one person's time in world.
 *
 * All of it comes from the desktop client's presence reports, the same arithmetic the Worlds page
 * uses, so it only covers time a moderator's client shared a room with them. The tab says so,
 * because "never seen" reads like "never there" and is nothing of the kind.
 */
function Metrics({ id }: { id: string }) {
  const load = useCallback(() => api.userMetrics(id), [id])
  const { data, error } = useLoad(load)

  if (error) return <Panel title="Metrics"><Note className="text-destructive">{error}</Note></Panel>
  if (!data) return <Panel title="Metrics"><Note>Loading…</Note></Panel>

  const c = data.counts

  return (
    <Panel title="Time in world">
      {!data.known ? (
        <Note>Not seen in an instance yet.</Note>
      ) : (
        <>
          <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
            <Figure label="Time seen" value={minutes(c.minutesSeen)} />
            <Figure label="Instances visited" value={compactNumber(c.rooms)} />
            <Figure label="Worlds visited" value={compactNumber(c.worlds)} />
            <Figure label="Arrivals" value={compactNumber(c.arrivals)} />
            <Figure
              label="Last seen"
              value={c.lastSeenAt ? ago(c.lastSeenAt, data.now) : '—'}
              note={c.lastSeenAt ? dateTime(c.lastSeenAt) : undefined}
            />
            <Figure label="First seen" value={c.firstSeenAt ? formatDay(c.firstSeenAt) : '—'} />
          </div>

          <div className="mt-2 font-medium">Instances they were seen in</div>
          {data.recentRooms.length === 0 ? (
            <Note>No instances yet.</Note>
          ) : (
            <RoomTable rooms={data.recentRooms} />
          )}
        </>
      )}
    </Panel>
  )
}

/**
 * Membership and ban standing, as the sweeps last read them, with how old that reading is.
 *
 * "Not a member" from a list synced an hour ago and "not a member" from a list still being read
 * for the first time are different claims, so the age travels with the answer.
 */
function MembershipCard({ subjectId }: { subjectId: string }) {
  const load = useCallback(() => api.membership(subjectId), [subjectId])
  const { data: view, error } = useLoad(load)

  return (
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="font-medium">Membership</div>

      {error && <p className="mt-1 text-destructive">{error}</p>}
      {!error && !view && <p className="mt-1 text-muted-foreground">Loading…</p>}

      {view && (
        <div className="mt-1 flex flex-col gap-1.5">
          {!view.members.firstSweepComplete ? (
            <p className="text-warn">Member list not read yet.</p>
          ) : view.isMember ? (
            <p>
              Member{view.joinedAt ? <> since {formatDay(view.joinedAt)}</> : ''}
              {view.isRepresenting ? ', representing the group' : ''}.
            </p>
          ) : view.known ? (
            <p>
              Not a member{view.leftAt ? <> — left {formatDay(view.leftAt)}</> : ''}
              {view.joinedAt ? <>, had joined {formatDay(view.joinedAt)}</> : ''}.
            </p>
          ) : (
            <p className="text-muted-foreground">Not a member.</p>
          )}

          {view.roleNames.length > 0 && (
            <div className="flex flex-wrap items-center gap-1">
              <span className="text-muted-foreground">Roles:</span>
              {view.roleNames.map((name, i) => (
                <Badge key={view.roleIds[i] ?? name} variant="secondary" title={view.roleIds[i]}>
                  {name}
                </Badge>
              ))}
            </div>
          )}

          {view.managerNotes && (
            <p className="text-muted-foreground">
              Manager notes:{' '}
              <span className="whitespace-pre-wrap break-words text-foreground">{view.managerNotes}</span>
            </p>
          )}

          {view.banned ? (
            <p className="text-destructive">
              On the ban list{view.bannedAt ? <> since {formatDay(view.bannedAt)}</> : ''}.
            </p>
          ) : view.banLiftedAt ? (
            <p className="text-muted-foreground">
              Was banned{view.bannedAt ? <> on {formatDay(view.bannedAt)}</> : ''}; lifted by{' '}
              {formatDay(view.banLiftedAt)}.
            </p>
          ) : !view.bans.firstSweepComplete ? (
            <p className="text-muted-foreground">Ban list not read yet.</p>
          ) : null}

          <p className="text-muted-foreground">
            Member list synced {ago(view.members.lastSyncedAt, view.members.now)}; ban list synced{' '}
            {ago(view.bans.lastSyncedAt, view.bans.now)}.
          </p>
        </div>
      )}
    </div>
  )
}
