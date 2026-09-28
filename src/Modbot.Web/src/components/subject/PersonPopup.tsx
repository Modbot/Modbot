import { useCallback, useRef, useState } from 'react'
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
import { ModerationActions, ModerationDialog } from '@/components/moderation/ModerationActions'
import { PersonFlags } from '@/components/subject/PersonFlags'
import { PersonNotes } from '@/components/subject/PersonNotes'
import { ProfileVersions } from '@/components/subject/ProfileVersions'
import { PhoneActions, StandingBar } from '@/components/subject/Standing'
import { Block, CopyId, Empty, FactList, More, Panel, PopupFrame } from '@/components/subject/shared'
import { ProfileBadges } from '@/components/ProfileBadges'
import { EmptyRow } from '@/components/PanelGrid'
import { Ago, Unread } from '@/components/Freshness'
import { Stat, StatStrip } from '@/pages/analytics/shared'
import { useDiscordRecords } from '@/lib/useDiscordRecords'
import { useLoad } from '@/lib/useLoad'
import {
  api,
  type AuditEntry,
  type CurrentUser,
  type MembershipView,
  type PersonMetrics,
  type PersonView,
} from '@/lib/api'
import { useDemo } from '@/lib/demo'
import { ago, formatDay, notLinkedTo, oldestReading } from '@/lib/format'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { can, canAny } from '@/lib/permissions'
import { actionsFor } from '@/lib/moderationActions'
import { useMessageAt, useOpeningAction, useOpeningTab, useOpeningVersion, type Subject } from '@/lib/subject'
import { useDiscordMember } from '@/lib/useDiscordMember'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { useStoredProfile, type StoredProfile } from '@/lib/useStoredProfile'

const TABS = ['overview', 'logs', 'notes', 'history', 'cases', 'flags', 'discord', 'messages', 'account', 'metrics', 'json'] as const
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
 * **Tabs by account, plus one merged Activity.** *What happened to this person* wants the merge, so
 * **Activity** is every fact about or by any of their accounts, each row naming the account it was
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

  // On a phone the tabs come after the whole profile, so a tab opened from the row under the title
  // or from the Note button at the foot would change somewhere the reader cannot see. Opening one
  // from there brings the tabs up to the top of the screen as well.
  const tabsAt = useRef<HTMLDivElement>(null)
  const openFromAbove = (next: Tab) => {
    setTab(next)
    if (window.matchMedia('(max-width: 767px)').matches)
      requestAnimationFrame(() => tabsAt.current?.scrollIntoView({ block: 'start' }))
  }

  // Bumped after a kick, ban or unban, or a note written or taken back, which remounts the cards
  // that read what Modbot stores.
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

  // Read here rather than in the Membership card, because the row under the title and the phone's
  // action row want the same answer, and three reads of one thing could disagree for a moment.
  // Both counters only go up, so their sum changes whenever either does.
  const loadMembership = useCallback(() => api.membership(vrchatId!), [vrchatId])
  const membership = useLoad(vrchatId && seesMembers ? loadMembership : null, acted + live)

  const [act, putAwayAction] = useOpeningAction()
  const askedAction =
    vrchatId && membership.data
      ? (actionsFor(me, { userId: vrchatId, banned: membership.data.banned, isMember: membership.data.isMember }).find(
          (o) => o.action === act,
        )?.action ?? null)
      : null

  const tabs: { value: Tab; label: string }[] = [
    { value: 'overview', label: 'Overview' },
    { value: 'logs', label: 'Activity' },
    ...(notesId && readsNotes ? [{ value: 'notes' as const, label: 'Notes' }] : []),
    ...(vrchatId && seesProfile ? [{ value: 'history' as const, label: 'Profile changes' }] : []),
    ...(vrchatId && seesProfile ? [{ value: 'cases' as const, label: 'Cases' }] : []),
    ...((vrchatId || discordId) && seesProfile ? [{ value: 'flags' as const, label: 'Flags' }] : []),
    ...(discordId && seesMembers ? [{ value: 'discord' as const, label: 'Discord' }] : []),
    ...(discordId && readsMessages ? [{ value: 'messages' as const, label: 'Messages' }] : []),
    ...(account && readsLogs ? [{ value: 'account' as const, label: 'Account' }] : []),
    ...(seesProfile ? [{ value: 'metrics' as const, label: 'Metrics' }] : []),
    { value: 'json', label: 'JSON' },
  ]

  // Led by the name, as VRChat's profile page and Discord's profile card are: "Person" and an id
  // told a moderator nothing, and the name was under the banner, a whole phone screen down (site
  // review 2026-09-27, finding 4). The id only stands in when no name is known.
  const shownId = vrchatId ?? discordId ?? at.id
  const name = stored.profile?.displayName ?? person.vrChat?.name ?? person.discord?.name ?? null

  // The accounts that could not be tied to this person, said in one row rather than a panel each,
  // which on a phone pushed Membership below the first screen. Each is only named where its own
  // card would have been drawn: a Modbot account is not even said to be absent unless the caller
  // may be told whether one exists.
  const missing = [
    ...(vrchatId === null ? ['VRChat'] : []),
    ...(person.discord === null && seesProfile ? ['Discord'] : []),
    ...(account === null && person.canSeeAccount ? ['Modbot'] : []),
  ]

  return (
    <PopupFrame
      title={name ?? shownId}
      subtitle={
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
          {vrchatId && (
            <ProfileBadges tags={null} lastPlatform={stored.profile?.lastPlatform} rank={stored.profile?.trustRank} />
          )}
          <CopyId id={shownId} />
        </span>
      }
      lead={lead}
      standing={
        <StandingBar
          person={person}
          me={me}
          membership={vrchatId && seesMembers ? membership : null}
          version={acted + live}
          notesId={notesId}
          notesPlatform={notesPlatform}
          // A chip only opens a tab this account has; the Banned chip needs only the member list,
          // and the Cases tab it points at needs the profile permission as well.
          onOpen={(next) => {
            if (tabs.some((t) => t.value === next)) openFromAbove(next)
          }}
        />
      }
      foot={
        <PhoneActions
          me={me}
          vrchatId={vrchatId}
          name={person.vrChat?.name ?? null}
          membership={vrchatId && seesMembers ? membership.data : null}
          canNote={notesId !== null && readsNotes}
          onNote={() => openFromAbove('notes')}
          onActed={() => setActed((n) => n + 1)}
        />
      }
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

          {missing.length > 0 && <Empty>{notLinkedTo(missing)}</Empty>}

          {person.discord && seesProfile && (
            <DiscordLinkCard key={live} side={person.discord} vrchatUserId={vrchatId} me={me} />
          )}

          {account && <AccountCard account={account} />}

          {vrchatId && seesMembers && (
            <MembershipCard
              subjectId={vrchatId}
              view={membership.data}
              error={membership.error}
              me={me}
              onActed={() => setActed((n) => n + 1)}
            />
          )}
        </>
      }
    >
      <div ref={tabsAt} className="flex min-h-0 flex-1 flex-col">
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
              onChanged={() => setActed((n) => n + 1)}
            />
          )}
          {tab === 'history' && vrchatId && <ProfileVersions key={live} id={vrchatId} openAt={version} />}
          {tab === 'cases' && vrchatId && <SubjectCaseFiles key={live} subjectId={vrchatId} />}
          {tab === 'flags' && (vrchatId || discordId) && (
            <PersonFlags key={live} vrchatId={vrchatId} discordId={discordId} />
          )}
          {tab === 'discord' && discordId && <DiscordHistory key={live} id={discordId} read={member} />}
          {tab === 'messages' && discordId && <DiscordMessages id={discordId} at={message} />}
          {tab === 'account' && account && <AccountHistory key={fresh} accountId={account.id} />}
          {tab === 'metrics' && <Metrics key={live} person={person} />}
          {tab === 'json' && <Records key={fresh} person={person} me={me} />}
        </Tabs>
      </div>

      {/* Opened by the palette's "Ban X…". The member list read above decides, by the same rule as
          the buttons, whether it is still an action to offer: somebody banned since the search is
          not asked about a second ban. */}
      <ModerationDialog
        me={me}
        action={askedAction}
        person={{ userId: vrchatId, banned: membership.data?.banned, isMember: membership.data?.isMember }}
        name={name ?? vrchatId}
        onClose={putAwayAction}
        onDone={() => setActed((n) => n + 1)}
      />
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

      <Panel title="Latest" right={<More onClick={() => onMore('logs')}>All activity</More>} flush>
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
  view,
  error,
  me,
  onActed,
}: {
  subjectId: string
  view: MembershipView | null
  error: string | null
  me: CurrentUser
  onActed: () => void
}) {
  const demo = useDemo()

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
            // The day Modbot lifted it, or the day the ban-list check found it gone. The two are
            // one field, and a check runs often enough that the day is the same either way.
            <p className="text-muted-foreground">
              {view.bannedAt ? (
                <>
                  Banned <span className="font-mono">{formatDay(view.bannedAt)}</span>, lifted
                </>
              ) : (
                'Ban lifted'
              )}{' '}
              <span className="font-mono">{formatDay(view.banLiftedAt)}</span>.
            </p>
          ) : !view.bans.firstSweepComplete ? (
            <p className="text-muted-foreground">Ban list not read yet.</p>
          ) : null}

          <p className="text-muted-foreground">{demo ? 'Demo data.' : <Checked view={view} />}</p>

          {/* On a phone these are in the row pinned to the foot of the popup instead. */}
          <div className="hidden md:block">
            <ModerationActions
              me={me}
              person={{ userId: subjectId, banned: view.banned, isMember: view.isMember }}
              name={profile?.displayName ?? subjectId}
              onDone={onActed}
              size="xs"
            />
          </div>
        </div>
      )}
    </Panel>
  )
}

/**
 * How old the card's reading is. The member list and the ban list are read separately, and the
 * card is only as fresh as the older of the two, so that is the age it gives (member and ban sync
 * design §6).
 */
function Checked({ view }: { view: MembershipView }) {
  const oldest = oldestReading([
    { at: view.members.lastSyncedAt, now: view.members.now },
    { at: view.bans.lastSyncedAt, now: view.bans.now },
  ])

  if (!oldest) return <>Not checked yet</>

  return (
    <>
      Checked <Ago iso={oldest.at} now={oldest.now} />
    </>
  )
}
