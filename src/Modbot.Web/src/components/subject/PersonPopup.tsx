import { useCallback, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Tabs } from '@/components/ui/tabs'
import { compactNumber, dateTime, minutes } from '@/components/charts'
import { JsonView } from '@/components/JsonView'
import { InstanceTable } from '@/components/InstanceTable'
import { SubjectCaseFiles } from '@/components/SubjectCaseFiles'
import { SubjectHistory } from '@/components/SubjectHistory'
import { ProfileDetails, ProfileIdentity } from '@/components/UserProfileCard'
import { AccountCard, AccountHistory } from '@/components/subject/AccountSide'
import { DiscordLinkCard } from '@/components/subject/DiscordLinkCard'
import {
  DiscordHistory,
  DiscordIdentity,
  DiscordMessages,
  DiscordMetrics,
} from '@/components/subject/DiscordSide'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { PersonNotes } from '@/components/subject/PersonNotes'
import { ProfileVersions } from '@/components/subject/ProfileVersions'
import { Block, Empty, FactList, More, Panel, PopupFrame } from '@/components/subject/shared'
import { EmptyRow } from '@/components/PanelGrid'
import { Ago, Unread } from '@/components/Freshness'
import { Stat, StatStrip } from '@/pages/analytics/shared'
import { useDiscordRecords } from '@/lib/useDiscordRecords'
import { useLoad } from '@/lib/useLoad'
import { api, type AuditEntry, type CurrentUser, type PersonMetrics, type PersonView } from '@/lib/api'
import { useDemo } from '@/lib/demo'
import { ago, formatDay } from '@/lib/format'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { can, canAny } from '@/lib/permissions'
import { useMessageAt, useOpeningTab, useOpeningVersion, type Subject } from '@/lib/subject'
import { useDiscordMember } from '@/lib/useDiscordMember'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { useStoredProfile, type StoredProfile } from '@/lib/useStoredProfile'

const TABS = ['overview', 'logs', 'notes', 'history', 'cases', 'discord', 'messages', 'account', 'metrics', 'json'] as const
type Tab = (typeof TABS)[number]

/**
 * One person: every account Modbot can tie to them, and everything recorded about any of them.
 *
 * **Three addresses, one view.** `?subject=usr_…`, `?subject=discord-person:…` and
 * `?subject=account:…` all open this, because all three name the same human being. The server ties
 * them together from whichever one arrived (`GET /api/people`); nothing here guesses, and an
 * account that ties to nothing still opens, showing what is known and saying plainly what is not
 * (one view per person design §3).
 *
 * **Tabs by account, plus one merged Logs.** *What happened to this person* wants the merge, so
 * **Logs** is every fact about or by any of their accounts, each row naming the account it was
 * found under. *What did this moderator do* and *what did they write in Discord* want neither
 * merged nor interleaved, so **Account**, **Discord** and **Messages** stay whole (design §4).
 *
 * **Each part is gated on the permission that part already needed.** What this account may not
 * read is left out rather than drawn empty, and a Modbot account is not even said to be absent
 * unless the caller may be told whether one exists.
 */
export function PersonPopup({ subject, me, lead }: { subject: Subject; me: CurrentUser; lead?: React.ReactNode }) {
  const { kind, id } = subject
  const load = useCallback(() => api.person(askOf(kind, id)), [kind, id])
  const { data: person, error } = useLoad(load)

  if (error)
    return (
      <PopupFrame title="Person" lead={lead} left={<Empty tone="danger">{error}</Empty>}>
        <div />
      </PopupFrame>
    )

  if (!person)
    return (
      <PopupFrame title="Person" lead={lead} left={<Empty>Loading…</Empty>}>
        <div />
      </PopupFrame>
    )

  return <Resolved person={person} me={me} lead={lead} at={subject} />
}

/** Which id the address carried. Exactly one, so the server knows what it was given. */
function askOf(kind: Subject['kind'], id: string) {
  if (kind === 'discord-person') return { discord: id }
  if (kind === 'account') return { account: id }
  return { vrchat: id }
}

function Resolved({
  person,
  me,
  lead,
  at,
}: {
  person: PersonView
  me: CurrentUser
  lead?: React.ReactNode
  at: Subject
}) {
  const vrchatId = person.vrChat?.id ?? null
  const discordId = person.discord?.id ?? null
  const account = person.account

  const seesProfile = can(me, 'ViewProfile')
  const seesMembers = can(me, 'ViewMembers')
  const readsMessages = can(me, 'ReadDiscordMessages')
  const readsLogs = canAny(me, ['ViewAuditLog', 'ViewOperationalLog'])

  // Notes are facts in the moderation log, so the log's own permission is what opens them --
  // there is no second, looser door onto the same rows (notes design §4).
  const readsNotes = can(me, 'ViewAuditLog')

  // Which account the notes are filed under. A note is about a person, but it is stored against
  // one of their accounts, so the tab asks for the VRChat one where there is one and falls back to
  // Discord for somebody Modbot only knows from there.
  const notesId = vrchatId ?? discordId
  const notesPlatform = vrchatId ? 'VRChat' : 'Discord'

  // Opened at one Discord message, from a source chip under a Chat answer: the Messages tab, on
  // the page that holds it, with that message marked.
  const message = useMessageAt()
  const version = useOpeningVersion()

  const opening: Tab = message && discordId && readsMessages ? 'messages' : 'overview'
  const [tab, setTab] = useOpeningTab<Tab>(opening, TABS)

  // Bumped after a kick, ban or unban, which remounts the cards that read what Modbot stores.
  // The server has already written the change, so this reads it back rather than guessing at it.
  const [acted, setActed] = useState(0)

  // And whenever a fact about any of this person's accounts lands on the live stream.
  const live = useLiveVersion(
    useCallback(
      (event: LiveEvent) =>
        (vrchatId ? concernsPerson(event, vrchatId) : false)
        || (discordId ? concernsPerson(event, discordId, 'Discord') : false),
      [vrchatId, discordId],
    ),
  )
  const fresh = `${acted}-${live}`

  const stored = useStoredProfile(vrchatId ?? '', live)
  const member = useDiscordMember(discordId, seesMembers)

  const tabs: { value: Tab; label: string }[] = [
    { value: 'overview', label: 'Overview' },
    { value: 'logs', label: 'Logs' },
    ...(notesId && readsNotes ? [{ value: 'notes' as const, label: 'Notes' }] : []),
    ...(vrchatId && seesProfile ? [{ value: 'history' as const, label: 'History' }] : []),
    ...(vrchatId && seesProfile ? [{ value: 'cases' as const, label: 'Cases' }] : []),
    ...(discordId && seesMembers ? [{ value: 'discord' as const, label: 'Discord' }] : []),
    ...(discordId && readsMessages ? [{ value: 'messages' as const, label: 'Messages' }] : []),
    ...(account && readsLogs ? [{ value: 'account' as const, label: 'Account' }] : []),
    ...(seesProfile ? [{ value: 'metrics' as const, label: 'Metrics' }] : []),
    { value: 'json', label: 'JSON' },
  ]

  return (
    <PopupFrame
      title="Person"
      // The id verbatim and unparsed: VRChat ids are opaque, and a legacy one looks nothing like
      // a modern one (spec 3.1.1).
      subtitle={<span className="font-mono" title={at.id}>{vrchatId ?? discordId ?? at.id}</span>}
      lead={lead}
      left={
        <>
          {vrchatId ? (
            stored.error ? (
              <Empty tone="danger">{stored.error}</Empty>
            ) : !stored.profile ? (
              <Empty>Loading…</Empty>
            ) : (
              <Block>
                <ProfileIdentity stored={stored} me={me} />
              </Block>
            )
          ) : discordId && seesMembers ? (
            member.error ? (
              <Empty tone="danger">{member.error}</Empty>
            ) : !member.data ? (
              <Empty>Loading…</Empty>
            ) : (
              <Block>
                <DiscordIdentity read={member} />
              </Block>
            )
          ) : null}

          {vrchatId === null && <Empty>No VRChat account.</Empty>}

          {person.discord && seesProfile && (
            <DiscordLinkCard key={live} side={person.discord} vrchatUserId={vrchatId} me={me} />
          )}
          {person.discord === null && seesProfile && <Empty>No Discord account.</Empty>}

          {account && <AccountCard account={account} />}
          {account === null && person.canSeeAccount && <Empty>No Modbot account.</Empty>}

          {vrchatId && seesMembers && (
            <MembershipCard key={fresh} subjectId={vrchatId} me={me} onActed={() => setActed((n) => n + 1)} />
          )}
        </>
      }
    >
      <Tabs value={tab} onChange={setTab} tabs={tabs} className="flex-1">
        {tab === 'overview' && (
          <Overview key={fresh} person={person} me={me} stored={stored} onMore={setTab} />
        )}
        {tab === 'logs' && <Logs key={fresh} person={person} />}
        {tab === 'notes' && notesId && (
          <PersonNotes
            key={`${notesId}-${live}`}
            subjectId={notesId}
            name={person.vrChat?.name ?? person.discord?.name}
            platform={notesPlatform}
          />
        )}
        {tab === 'history' && vrchatId && <ProfileVersions key={live} id={vrchatId} openAt={version} />}
        {tab === 'cases' && vrchatId && <SubjectCaseFiles key={live} subjectId={vrchatId} />}
        {tab === 'discord' && discordId && <DiscordHistory key={live} id={discordId} read={member} />}
        {tab === 'messages' && discordId && <DiscordMessages id={discordId} at={message} />}
        {tab === 'account' && account && <AccountHistory key={fresh} accountId={account.id} />}
        {tab === 'metrics' && <Metrics key={live} person={person} />}
        {tab === 'json' && <Records key={fresh} person={person} me={me} />}
      </Tabs>
    </PopupFrame>
  )
}

/** What each fact was found under, for the merged list. */
const VRCHAT = 'VRChat'
const DISCORD = 'Discord'
const ACCOUNT = 'Modbot account'

type MergedFacts = { entries: AuditEntry[]; from: Map<number, string> }

/**
 * Every fact about or by any of this person's accounts, newest first.
 *
 * One read per account per side, merged here rather than on the server: the log filters subject
 * and actor separately, and the newest N of each merged and cut to N are exactly the newest N of
 * all of them. The Modbot account needs only one read, because `?account=` answers both halves.
 *
 * Which account each row was found under travels with it. The merge is a convenience, not a
 * claim: a Discord row is still a Discord row.
 */
function usePersonFacts(person: PersonView, limit: number) {
  const vrchatId = person.vrChat?.id ?? null
  const discordId = person.discord?.id ?? null
  const accountId = person.account?.id ?? null

  const load = useCallback(async (): Promise<MergedFacts> => {
    const asks: { from: string; page: Promise<{ entries: AuditEntry[] }> }[] = []

    if (vrchatId) {
      asks.push({ from: VRCHAT, page: api.audit({ subject: vrchatId, subjectPlatform: 'VRChat', limit }) })
      asks.push({ from: VRCHAT, page: api.audit({ actor: vrchatId, actorPlatform: 'VRChat', limit }) })
    }

    if (discordId) {
      asks.push({ from: DISCORD, page: api.audit({ subject: discordId, subjectPlatform: 'Discord', limit }) })
      asks.push({ from: DISCORD, page: api.audit({ actor: discordId, actorPlatform: 'Discord', limit }) })
    }

    if (accountId) asks.push({ from: ACCOUNT, page: api.audit({ account: accountId, limit }) })

    const pages = await Promise.all(asks.map((a) => a.page))

    const from = new Map<number, string>()
    const seen = new Set<number>()
    const entries: AuditEntry[] = []

    pages.forEach((page, i) => {
      for (const entry of page.entries) {
        if (seen.has(entry.id)) continue
        seen.add(entry.id)
        from.set(entry.id, asks[i].from)
        entries.push(entry)
      }
    })

    entries.sort((a, b) => Date.parse(b.occurredAt) - Date.parse(a.occurredAt) || b.id - a.id)
    return { entries: entries.slice(0, limit), from }
  }, [vrchatId, discordId, accountId, limit])

  return useLoad(load)
}

/** The glance: the profile, how often they have been acted on, where they have been, and the newest facts. */
function Overview({
  person,
  me,
  stored,
  onMore,
}: {
  person: PersonView
  me: CurrentUser
  stored: StoredProfile
  onMore: (tab: Tab) => void
}) {
  const seesProfile = can(me, 'ViewProfile')
  const vrchatId = person.vrChat?.id ?? null

  const facts = usePersonFacts(person, 8)

  const loadMetrics = useCallback(() => api.userMetrics(vrchatId!), [vrchatId])
  const metrics = useLoad(seesProfile && vrchatId ? loadMetrics : null)

  return (
    <div className="flex flex-col">
      {seesProfile && stored.profile?.known && stored.profile.lastRefreshedAt && (
        <Panel title="Profile">
          <ProfileDetails stored={stored} />
        </Panel>
      )}

      {seesProfile && vrchatId && <SubjectHistory subjectId={vrchatId} />}

      {metrics.data?.known && (
        <StatStrip className="m-0 shrink-0">
          <Stat label="Time seen" value={minutes(metrics.data.counts.minutesSeen)} />
          <Stat label="Instances visited" value={compactNumber(metrics.data.counts.instances)} />
          <Stat label="Worlds visited" value={compactNumber(metrics.data.counts.worlds)} />
          <Stat
            label="Last seen"
            value={metrics.data.counts.lastSeenAt ? ago(metrics.data.counts.lastSeenAt, metrics.data.now) : '—'}
            note={metrics.data.counts.lastSeenAt ? dateTime(metrics.data.counts.lastSeenAt) : undefined}
            noteMono
          />
        </StatStrip>
      )}

      <Panel title="Latest" right={<More onClick={() => onMore('logs')}>All logs</More>} flush>
        {facts.error && <EmptyRow tone="danger">{facts.error}</EmptyRow>}
        {!facts.error && !facts.data && <EmptyRow>Loading…</EmptyRow>}
        {facts.data && (
          <FactList
            entries={facts.data.entries}
            empty="Nothing recorded yet."
            from={(entry) => facts.data!.from.get(entry.id)}
          />
        )}
      </Panel>
    </div>
  )
}

function Logs({ person }: { person: PersonView }) {
  const { data, error } = usePersonFacts(person, 50)

  return (
    <Panel title="Everything recorded about this person" flush>
      {error && <EmptyRow tone="danger">{error}</EmptyRow>}
      {!error && !data && <EmptyRow>Loading…</EmptyRow>}
      {data && (
        <FactList entries={data.entries} empty="Nothing recorded yet." from={(entry) => data.from.get(entry.id)} />
      )}
    </Panel>
  )
}

/**
 * The stored records, verbatim, as one document.
 *
 * Six panels stacked down the tab was six copy buttons and six scrollbars for what is one
 * person, and answering "what does Modbot hold about them" meant reading all six and joining
 * them by eye. They are named keys of one record now, so the tab is read once and copied once.
 *
 * A key a person may not read is left out rather than written as null, which is the same rule
 * the panels followed: absent means "not yours to see or not there", and the two are not
 * distinguished here any more than they were before.
 */
function Records({ person, me }: { person: PersonView; me: CurrentUser }) {
  const seesProfile = can(me, 'ViewProfile')
  const seesMembers = can(me, 'ViewMembers')
  const vrchatId = person.vrChat?.id ?? null
  const discordId = person.discord?.id ?? null

  const loadProfile = useCallback(() => api.userProfile(vrchatId!), [vrchatId])
  const profile = useLoad(seesProfile && vrchatId ? loadProfile : null)

  const loadRaw = useCallback(() => api.userRaw(vrchatId!), [vrchatId])
  const raw = useLoad(seesProfile && vrchatId ? loadRaw : null)

  const loadMembership = useCallback(() => api.membership(vrchatId!), [vrchatId])
  const membership = useLoad(seesMembers && vrchatId ? loadMembership : null)

  const discord = useDiscordRecords(discordId, me)

  const records: Record<string, unknown> = {}

  if (vrchatId && seesProfile) records.profile = profile.error ?? profile.data
  if (vrchatId && seesMembers) records.membership = membership.error ?? membership.data
  if (vrchatId && seesProfile) {
    records.vrchatPublicProfile = raw.error ?? raw.data?.publicProfile
    records.vrchatUser = raw.error ?? raw.data?.user
  }
  if (discord.member !== undefined) records.discordMember = discord.member
  if (discord.activity !== undefined) records.discordActivity = discord.activity
  if (person.account) records.modbotAccount = person.account

  return (
    <div className="flex min-h-0 flex-col">
      {Object.keys(records).length > 0 ? (
        <JsonView title="Records" value={records} className="border-0" />
      ) : (
        <Empty>You do not have permission to see this.</Empty>
      )}
    </div>
  )
}

/**
 * What Modbot can actually work out about one person's time in world, and their activity in
 * Discord, side by side.
 *
 * The time-in-world figures come from the companion's presence reports, the same arithmetic the
 * Worlds page uses, so they only cover time a moderator's client shared an instance with them.
 */
function Metrics({ person }: { person: PersonView }) {
  const vrchatId = person.vrChat?.id ?? null
  const discordId = person.discord?.id ?? null

  const load = useCallback(() => api.userMetrics(vrchatId!), [vrchatId])
  const { data, error } = useLoad(vrchatId ? load : null)

  return (
    <div className="flex min-h-0 flex-col">
      {vrchatId && (
        <Panel title="Time in world" flush>
          {error && <EmptyRow tone="danger">{error}</EmptyRow>}
          {!error && !data && <EmptyRow>Loading…</EmptyRow>}
          {data && !data.known && <EmptyRow>Not seen in an instance yet.</EmptyRow>}
          {data?.known && <TimeInWorld data={data} />}
        </Panel>
      )}

      {data?.known && (
        <Panel title="Instances they were seen in" flush>
          {data.recentInstances.length === 0 ? (
            <EmptyRow>No instances yet.</EmptyRow>
          ) : (
            <InstanceTable instances={data.recentInstances} />
          )}
        </Panel>
      )}

      {discordId && <DiscordMetrics id={discordId} />}
    </div>
  )
}

function TimeInWorld({ data }: { data: PersonMetrics }) {
  const c = data.counts

  return (
    <StatStrip className="m-0 md:grid-cols-3 xl:grid-cols-3">
      <Stat label="Time seen" value={minutes(c.minutesSeen)} />
      <Stat label="Instances visited" value={compactNumber(c.instances)} />
      <Stat label="Worlds visited" value={compactNumber(c.worlds)} />
      <Stat label="Arrivals" value={compactNumber(c.arrivals)} />
      <Stat
        label="Last seen"
        value={c.lastSeenAt ? ago(c.lastSeenAt, data.now) : '—'}
        note={c.lastSeenAt ? dateTime(c.lastSeenAt) : undefined}
        noteMono
      />
      <Stat label="First seen" value={c.firstSeenAt ? formatDay(c.firstSeenAt) : '—'} />
    </StatStrip>
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

  const unread = view != null && !view.members.firstSweepComplete

  return (
    <Panel
      title="Membership"
      flush={!view}
      warn={unread}
      right={unread ? <Unread>Member list not read yet.</Unread> : undefined}
    >
      {error && <EmptyRow tone="danger">{error}</EmptyRow>}
      {!error && !view && <EmptyRow>Loading…</EmptyRow>}

      {view && (
        <div className="flex flex-col gap-1.5" style={{ fontSize: 'var(--text-small)' }}>
          {unread ? null : view.isMember ? (
            <p>
              Member{view.joinedAt ? <> since <span className="font-mono">{formatDay(view.joinedAt)}</span></> : ''}
              {view.isRepresenting ? ', representing the group' : ''}.
            </p>
          ) : view.known ? (
            <p>
              Not a member{view.leftAt ? <>, left <span className="font-mono">{formatDay(view.leftAt)}</span></> : ''}
              {view.joinedAt ? <>, had joined <span className="font-mono">{formatDay(view.joinedAt)}</span></> : ''}.
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
              On the ban list{view.bannedAt ? <> since <span className="font-mono">{formatDay(view.bannedAt)}</span></> : ''}.
            </p>
          ) : view.banLiftedAt ? (
            <p className="text-muted-foreground">
              Was banned{view.bannedAt ? <> on <span className="font-mono">{formatDay(view.bannedAt)}</span></> : ''}; lifted by{' '}
              <span className="font-mono">{formatDay(view.banLiftedAt)}</span>.
            </p>
          ) : !view.bans.firstSweepComplete ? (
            <p className="text-muted-foreground">Ban list not read yet.</p>
          ) : null}

          <p className="text-muted-foreground">
            {demo ? (
              'Demo data.'
            ) : (
              <>
                Member list synced <Ago iso={view.members.lastSyncedAt} now={view.members.now} />; ban list synced{' '}
                <Ago iso={view.bans.lastSyncedAt} now={view.bans.now} />.
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
    </Panel>
  )
}
