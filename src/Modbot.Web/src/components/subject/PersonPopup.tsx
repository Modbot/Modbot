import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Tabs } from '@/components/ui/tabs'
import { compactNumber, dateTime, minutes } from '@/components/charts'
import { JsonView } from '@/components/JsonView'
import { RoomTable } from '@/components/RoomTable'
import { SubjectCaseFiles } from '@/components/SubjectCaseFiles'
import { SubjectHistory } from '@/components/SubjectHistory'
import { UserProfileCard } from '@/components/UserProfileCard'
import { DiscordLinkCard } from '@/components/subject/DiscordLinkCard'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { ProfileVersions } from '@/components/subject/ProfileVersions'
import { FactList, Figure, Note, Panel, PopupFrame } from '@/components/subject/shared'
import { useLoad } from '@/lib/useLoad'
import { api, type CurrentUser } from '@/lib/api'
import { useDemo } from '@/lib/demo'
import { ago, formatDay } from '@/lib/format'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { can } from '@/lib/permissions'
import { useOpeningTab, useOpeningVersion } from '@/lib/subject'
import { useLiveVersion } from '@/lib/useLiveVersion'

const TABS = ['overview', 'logs', 'history', 'cases', 'metrics', 'json'] as const
type Tab = (typeof TABS)[number]

/**
 * One person: their VRChat profile on the left, and what Modbot has recorded about them on the
 * right.
 *
 * Overview is the glance: the repeat-offender counts, the presence figures and the newest facts.
 * Logs is every fact; History is the profile as it stood after each recorded change, replayed
 * from the facts; JSON is the stored records verbatim. The profile card, the history counts and
 * the case files are the same components the side pane used, moved rather than rewritten.
 */
export function PersonPopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  const seesProfile = can(me, 'ViewProfile')
  const [tab, setTab] = useOpeningTab<Tab>('overview', TABS)
  const version = useOpeningVersion()

  // Bumped after a kick, ban or unban, which remounts the cards that read what Modbot stores.
  // The server has already written the change, so this reads it back rather than guessing at it.
  const [acted, setActed] = useState(0)

  // And whenever a fact about this person lands on the live stream -- a join, a ban, a role, a
  // profile change -- for the same reason: the server has it, so read it back.
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id), [id]))
  const fresh = `${acted}-${live}`

  const tabs: { value: Tab; label: string }[] = [
    { value: 'overview', label: 'Overview' },
    { value: 'logs', label: 'Logs' },
    ...(seesProfile ? [{ value: 'history' as const, label: 'History' }] : []),
    ...(seesProfile ? [{ value: 'cases' as const, label: 'Cases' }] : []),
    ...(seesProfile ? [{ value: 'metrics' as const, label: 'Metrics' }] : []),
    { value: 'json', label: 'JSON' },
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
          <UserProfileCard key={live} subjectId={id} me={me} />
          {seesProfile && <DiscordLinkCard key={live} subjectId={id} me={me} />}

          {can(me, 'ViewMembers') && (
            <MembershipCard key={fresh} subjectId={id} me={me} onActed={() => setActed((n) => n + 1)} />
          )}
        </>
      }
    >
      <Tabs value={tab} onChange={setTab} tabs={tabs}>
        {tab === 'overview' && <Overview key={fresh} id={id} me={me} onMore={setTab} />}
        {tab === 'logs' && <Logs key={fresh} id={id} />}
        {tab === 'history' && <ProfileVersions key={live} id={id} openAt={version} />}
        {tab === 'cases' && (
          <div className="p-4">
            <SubjectCaseFiles key={live} subjectId={id} />
          </div>
        )}
        {tab === 'metrics' && <Metrics key={live} id={id} />}
        {tab === 'json' && <Records key={fresh} id={id} me={me} />}
      </Tabs>
    </PopupFrame>
  )
}

/** The glance: how often they have been acted on, where they have been, and the newest facts. */
function Overview({ id, me, onMore }: { id: string; me: CurrentUser; onMore: (tab: Tab) => void }) {
  const seesProfile = can(me, 'ViewProfile')

  const loadFacts = useCallback(() => api.audit({ subject: id, limit: 8 }), [id])
  const facts = useLoad(loadFacts)

  const loadMetrics = useCallback(() => api.userMetrics(id), [id])
  const metrics = useLoad(seesProfile ? loadMetrics : null)

  return (
    <div className="flex flex-col gap-3 p-4">
      {seesProfile && <SubjectHistory subjectId={id} />}

      {metrics.data?.known && (
        <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
          <Figure label="Time seen" value={minutes(metrics.data.counts.minutesSeen)} />
          <Figure label="Instances visited" value={compactNumber(metrics.data.counts.rooms)} />
          <Figure label="Worlds visited" value={compactNumber(metrics.data.counts.worlds)} />
          <Figure
            label="Last seen"
            value={metrics.data.counts.lastSeenAt ? ago(metrics.data.counts.lastSeenAt, metrics.data.now) : '—'}
            note={metrics.data.counts.lastSeenAt ? dateTime(metrics.data.counts.lastSeenAt) : undefined}
          />
        </div>
      )}

      <div className="flex items-center gap-2">
        <span className="font-medium">Latest</span>
        <span className="flex-1" />
        <button type="button" onClick={() => onMore('logs')} className="text-muted-foreground hover:text-foreground hover:underline" style={{ fontSize: 'var(--text-small)' }}>
          All logs
        </button>
      </div>

      {facts.error && <Note className="text-destructive">{facts.error}</Note>}
      {!facts.error && !facts.data && <Note>Loading…</Note>}
      {facts.data && <FactList entries={facts.data.entries} empty="Nothing recorded yet." />}
    </div>
  )
}

function Logs({ id }: { id: string }) {
  const load = useCallback(() => api.audit({ subject: id, limit: 50 }), [id])
  const { data, error } = useLoad(load)

  return (
    <div className="flex flex-col gap-3 p-4">
      <div className="font-medium">Everything recorded about this person</div>

      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </div>
  )
}

/**
 * The stored records, verbatim: the profile as the API answers it, the membership and ban
 * standing, and the bodies VRChat last sent. Each needs the permission the screen showing it
 * needs; what this account may not read is left out rather than shown empty.
 */
function Records({ id, me }: { id: string; me: CurrentUser }) {
  const seesProfile = can(me, 'ViewProfile')
  const seesMembers = can(me, 'ViewMembers')

  const loadProfile = useCallback(() => api.userProfile(id), [id])
  const profile = useLoad(seesProfile ? loadProfile : null)

  const loadRaw = useCallback(() => api.userRaw(id), [id])
  const raw = useLoad(seesProfile ? loadRaw : null)

  const loadMembership = useCallback(() => api.membership(id), [id])
  const membership = useLoad(seesMembers ? loadMembership : null)

  return (
    <div className="flex flex-col gap-3 p-4">
      {seesProfile && <JsonView title="Profile" value={profile.error ?? profile.data} />}
      {seesMembers && <JsonView title="Membership" value={membership.error ?? membership.data} />}
      {seesProfile && (
        <>
          <JsonView title="VRChat public profile, as last read" value={raw.error ?? raw.data?.publicProfile} />
          <JsonView title="VRChat user object, as last read" value={raw.error ?? raw.data?.user} />
        </>
      )}
      {!seesProfile && !seesMembers && <Note>You do not have permission to see this.</Note>}
    </div>
  )
}

/**
 * What Modbot can actually work out about one person's time in world.
 *
 * All of it comes from the companion's presence reports, the same arithmetic the Worlds page
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
function MembershipCard({
  subjectId,
  me,
  onActed,
}: {
  subjectId: string
  me: CurrentUser
  onActed: () => void
}) {
  const demo = useDemo()

  const load = useCallback(() => api.membership(subjectId), [subjectId])
  const { data: view, error } = useLoad(load)

  // The name for the confirmation. Read here rather than passed down, because the standing this
  // card already knows and the name are wanted in the same sentence.
  const loadProfile = useCallback(() => api.userProfile(subjectId), [subjectId])
  const { data: profile } = useLoad(loadProfile)

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
              Not a member{view.leftAt ? <>, left {formatDay(view.leftAt)}</> : ''}
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
            {demo ? (
              'Demo data.'
            ) : (
              <>
                Member list synced {ago(view.members.lastSyncedAt, view.members.now)}; ban list synced{' '}
                {ago(view.bans.lastSyncedAt, view.bans.now)}.
              </>
            )}
          </p>

          <ModerationActions
            me={me}
            person={{ userId: subjectId, banned: view.banned, isMember: view.isMember }}
            name={profile?.displayName ?? subjectId}
            onDone={onActed}
            size="xs"
          />
        </div>
      )}
    </div>
  )
}
