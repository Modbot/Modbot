import { AccountLink, PersonLink, InstanceLink, WorldLink } from '@/components/facts'
import { ChevronRight } from 'lucide-react'
import { JsonView } from '@/components/JsonView'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import type { AuditEntry } from '@/lib/api'
import { openPersonVersion } from '@/lib/subject'
import { avatarWorn, timeInInstance } from '@/lib/factDetails'
import { duration } from '@/lib/format'

/**
 * Every fact, as a sentence naming who did what to whom and where.
 *
 * A log row that reads `vrchat.group.instance.kick` is a row a volunteer moderator cannot use. It
 * should read *Mira kicked Ada out of The Black Cat #39047* — with every name in it clickable,
 * opening the person or the instance. The world's name and VRChat's number are one link, the way
 * VRChat shows an instance in game; a world Modbot has not read yet is named by its id.
 *
 * ## Written at read time, never stored
 *
 * Nothing here is ever written back to a fact. Facts are append-only and never mutated
 * (foundation §5.2), and they do not need to be: the audit-log producer already keeps VRChat's
 * whole entry in the payload — `auditEntryId`, `eventType`, `groupId`, `actorId`,
 * `actorDisplayName`, `targetId`, `createdAt`, `description` and the entire `auditData` node —
 * and an event Modbot had no name for is stored as `modbot.unrecognised` with VRChat's own word
 * kept in `typeRaw`.
 *
 * So this reads `type`, `typeRaw` and `data`, in that order of preference, and **the moment a
 * sentence is written here, every row ever recorded reads correctly.** That is the catch-up: no
 * job to run, no rows rewritten, nothing to schedule. Adding a sentence below improves the whole
 * of recorded history the next time somebody opens the page.
 *
 * Two limits worth stating rather than papering over:
 *
 * - A sentence can only say what the payload captured. Where a field was never kept, the sentence
 *   is thinner for old rows and cannot be made fuller — VRChat's audit log ages out, so the entry
 *   cannot be fetched again. Fixing the producer fixes it going forward only.
 * - Counting a newly-understood type towards a chart is a different thing and is not done here.
 *   The daily totals are recomputable from facts (§5.2), which is the path for that.
 *
 * ## Keyed on `typeRaw` as well as `type`
 *
 * A fact written before Modbot knew a word has `type` `modbot.unrecognised` and the source's own
 * word in `typeRaw`. Looking the word up here means such a row starts reading properly as soon as
 * a sentence exists for it, with the stored row untouched. Until then it says so plainly and
 * shows the raw event, so a moderator can still read what happened.
 */
export function FactSentence({ entry }: { entry: AuditEntry }) {
  const write = SENTENCES[entry.type] ?? (entry.typeRaw ? RAW[entry.typeRaw] : undefined)

  if (write) return <>{write(parts(entry))}</>

  return <>{fallback(parts(entry))}</>
}

// ── The pieces a sentence is assembled from ────────────────────────────────────────────────────

type Parts = {
  entry: AuditEntry
  /** The thing the fact is about, clickable when Modbot knows what it is. */
  subject: React.ReactNode
  /** Who did it, or "somebody" when the source named nobody. */
  actor: React.ReactNode
  /** True when the source named an actor — for sentences that read better in the passive without one. */
  hasActor: boolean
  world: React.ReactNode
  instance: React.ReactNode
  /**
   * Where it happened, as one link: "The Black Cat #39047" when the fact names an instance, the
   * world alone when it only names a world, null when it names neither.
   */
  place: React.ReactNode
  text: (key: string) => string | null
  changed: [string, { old?: unknown; new?: unknown }][]
}

/**
 * A thing by name, or the kind of thing when Modbot never learned its name.
 *
 * "the Discord role “Moderator”" when the name is there, "a Discord role" when it is not. Dropping a
 * placeholder into the sentence instead gives "the Discord role a role", which is not a sentence:
 * a name that is missing changes the shape of the clause around it, not just the word in the hole.
 */
function named(name: string | null | undefined, kind: string): React.ReactNode {
  return name ? <>the {kind} “{name}”</> : `a ${kind}`
}

/**
 * A Discord voice channel by name, or the general phrase when Modbot never learned it.
 *
 * Channels are named with a leading hash by everyone who uses Discord, so the name carries its own
 * punctuation and needs no word in front of it.
 */
function voice(name: string | null | undefined): React.ReactNode {
  return name ? <>the Discord voice channel {name}</> : 'a Discord voice channel'
}

/** A count in the payload, written out, or the given word when there is none. */
function howMany(p: Parts, otherwise: string): string {
  const value = p.entry.data?.['count']
  return typeof value === 'number' ? value.toLocaleString() : otherwise
}

function parts(entry: AuditEntry): Parts {
  const text = (key: string) => {
    const value = entry.data?.[key]
    return typeof value === 'string' && value.length > 0 ? value : null
  }

  const world = entry.worldId ? <WorldLink id={entry.worldId} name={entry.worldName} /> : null
  const instance = entry.instanceId ? (
    <InstanceLink
      modbotInstanceId={entry.modbotInstanceId}
      worldId={entry.worldId}
      worldName={entry.worldName}
      number={entry.instanceId}
    />
  ) : null

  return {
    entry,
    subject: <Subject entry={entry} />,
    actor: entry.actorId ? (
      <>
        <PersonLink platform={entry.actorPlatform} id={entry.actorId} name={entry.actorName} />
        <TrustRankBadge rank={entry.actorTrustRank} className="ml-1 align-middle" />
      </>
    ) : (
      <span className="text-muted-foreground">Somebody</span>
    ),
    hasActor: entry.actorId !== null,
    world,
    instance,
    place: instance ?? world,
    text,
    changed: changedFields(entry),
  }
}

/**
 * The subject, clickable according to what it is.
 *
 * A person opens their popup; an instance opens that instance; a world, a group, a role and everything
 * else are shown as text, because opening a popup on an id Modbot knows nothing about would be a
 * dead end dressed up as a link.
 */
function Subject({ entry }: { entry: AuditEntry }) {
  if (entry.subjectKind === 'Person')
    return (
      <>
        <PersonLink platform={entry.subjectPlatform} id={entry.subjectId} name={entry.subjectName} />
        <TrustRankBadge rank={entry.subjectTrustRank} className="ml-1 align-middle" />
      </>
    )

  if (entry.subjectKind === 'Instance')
    return entry.instanceId ? (
      <InstanceLink
        modbotInstanceId={entry.modbotInstanceId}
        worldId={entry.worldId}
        worldName={entry.worldName}
        number={entry.instanceId}
      />
    ) : (
      <Id value={entry.subjectId} />
    )

  // A Modbot account opens the person popup of whoever holds it, the same popup their VRChat name
  // opens: one human being, whichever of their accounts the fact happened to name.
  if (entry.subjectKind === 'Account')
    return <AccountLink id={entry.subjectId} name={entry.subjectName} />

  if (entry.subjectKind === 'Group') return <span className="font-medium">the group</span>

  return <Id value={entry.subjectId} />
}

/** An id nothing is known about. Shown verbatim, shortened, with the whole of it on hover. */
function Id({ value }: { value: string }) {
  return (
    <span className="font-mono text-muted-foreground" title={value}>
      {value.length > 24 ? `${value.slice(0, 24)}…` : value}
    </span>
  )
}

/** A field value from a `{old, new}` pair, as short readable text. */
function shown(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'nothing'
  if (typeof value === 'boolean') return value ? 'yes' : 'no'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

/** "member count" out of "MemberCount" or "memberCount" — the field, said the way a person would. */
function fieldName(key: string): string {
  const spaced = key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .toLowerCase()
    .trim()

  return spaced || key
}

/**
 * Fields a source changes as a side effect of any edit: who last touched it, and when. They say
 * nothing about what the edit was, so they are left out of the sentence. The row's details still
 * hold them.
 */
const BOOKKEEPING_FIELDS = new Set(['lastupdatedbyuserid', 'updatedat', 'createdat', 'lastpostcreatedat', 'id', 'groupid'])

function changedFields(entry: AuditEntry): [string, { old?: unknown; new?: unknown }][] {
  const changed = entry.data?.['changed']
  if (!changed || typeof changed !== 'object') return []

  return Object.entries(changed as Record<string, { old?: unknown; new?: unknown }>).filter(
    ([key]) => !BOOKKEEPING_FIELDS.has(key.toLowerCase()),
  )
}

/**
 * ": name from The Black Cat to The Red Cat; color from #FF0000 to #00FF00" — the diff the
 * producers all record the same way.
 *
 * Each field is said with what it was and what it became, so the row answers "what changed" on
 * its own. Past a handful of fields the sentence would stop being one, so only their names are
 * listed then, and the whole diff is a click away in the row's details.
 */
function Changed({ changed }: { changed: Parts['changed'] }) {
  const single = changed.filter(([, pair]) => !isListChange(pair))
  if (single.length === 0) return null

  if (single.length > 4) return <>: {list(single.map(([key]) => fieldName(key)))} changed</>

  return <>: {single.map(([key, pair]) => changePhrase(key, pair)).join('; ')}</>
}

/** A field whose value is a list, like a role's permissions or a person's tags. */
function isListChange(pair: { old?: unknown; new?: unknown }): boolean {
  return Array.isArray(pair.old) || Array.isArray(pair.new)
}

/**
 * The lists that changed, each on a line of its own after the sentence: "Gave it: …" and "Took
 * away: …" for permissions, "Added to tags: …" for anything else.
 *
 * A list is said as what was added and removed, never as its before and after: cut short they read
 * the same, and a whole list is not a sentence. The same two lines a Discord role edit reads, so a
 * role reads the same whichever platform it is on.
 */
function ChangedLists({ changed }: { changed: Parts['changed'] }) {
  return (
    <>
      {changed
        .filter(([, pair]) => isListChange(pair))
        .map(([key, pair]) => {
          const permissionList = /permissions/i.test(key)
          const item = permissionList ? permissionLabel : listItem
          const was = (Array.isArray(pair.old) ? pair.old : []).map(item)
          const now = (Array.isArray(pair.new) ? pair.new : []).map(item)
          const added = now.filter((v) => !was.includes(v))
          const removed = was.filter((v) => !now.includes(v))
          const field = fieldName(key)

          if (added.length === 0 && removed.length === 0)
            return (
              <span key={key} className="block">
                {field.charAt(0).toUpperCase()}
                {field.slice(1)} put in a different order.
              </span>
            )

          return (
            <span key={key} className="contents">
              <Bullets heading={permissionList ? 'Gave it' : `Added to ${field}`} items={added} />
              <Bullets heading={permissionList ? 'Took away' : `Removed from ${field}`} items={removed} />
            </span>
          )
        })}
    </>
  )
}

/**
 * A bulleted heading, then one entry to an indented bullet under it. A list read as a sentence
 * ("A, B, C and D") is hard to run an eye down; bullets are not.
 *
 * Spans shown as list items rather than a `<ul>`, because a sentence also sits inside a
 * `<summary>`, which may only hold inline content.
 */
function Bullets({ heading, items }: { heading: string; items: string[] }) {
  if (items.length === 0) return null

  return (
    <span className="mt-1 ml-5 list-item list-disc">
      {heading}:
      {items.map((item) => (
        <span key={item} className="ml-5 list-item list-[circle]">
          {item}
        </span>
      ))}
    </span>
  )
}

/**
 * One field's change, for a field holding a single value.
 */
function changePhrase(key: string, pair: { old?: unknown; new?: unknown }): string {
  const say = (value: unknown) =>
    typeof value === 'string' && value && NAMING_FIELD.test(key) ? `“${clipped(value)}”` : clipped(shown(value))

  return `${fieldName(key)} from ${say(pair.old)} to ${say(pair.new)}`
}

/** Fields whose value is somebody's own words, quoted so they cannot run into the sentence. */
const NAMING_FIELD = /name|title|topic|description|bio|status|pronouns/i

/** One entry of a changed list, said the way a person would: "new-member" as "new member". */
function listItem(value: unknown): string {
  return typeof value === 'string' ? fieldName(value) : JSON.stringify(value)
}

/**
 * VRChat's group permissions as VRChat's own role editor labels them (read from the group settings
 * page on vrchat.com, 2026-09-25). VRChat's API names them by id alone. `*` is the owner's "all
 * permissions", which the editor does not list. An id this build does not know is spelled out.
 */
const VRCHAT_PERMISSIONS: Record<string, string> = {
  '*': 'Every permission',
  'group-members-manage': 'Manage Group Member Data',
  'group-data-manage': 'Manage Group Data',
  'group-audit-view': 'View Audit log',
  'group-roles-manage': 'Manage Group Roles',
  'group-default-role-manage': 'Manage Group Default Role',
  'group-roles-assign': 'Assign Group Roles',
  'group-bans-manage': 'Manage Group Bans',
  'group-members-remove': 'Remove Group Members',
  'group-members-viewall': 'View All Members',
  'group-announcement-manage': 'Manage Group Announcement',
  'group-instance-announcement-create': 'Create Instance Announcement',
  'group-calendar-manage': 'Manage Group Calendar',
  'group-instance-calendar-link': 'Link Instances and Events',
  'group-galleries-manage': 'Manage Group Galleries',
  'group-invites-manage': 'Manage Group Invites',
  'group-instance-moderate': 'Moderate Group Instances',
  'group-instance-manage': 'Manage Group Instances',
  'group-instance-queue-priority': 'Group Instance Queue Priority',
  'group-instance-age-gated-create': 'Create Age Gated Instances',
  'group-instance-public-create': 'Create Group Public Instances',
  'group-instance-plus-create': 'Create Group+ Instances',
  'group-instance-open-create': 'Create Members-Only Group Instances',
  'group-instance-restricted-create': 'Role-Restrict Members-Only Instances',
  'group-instance-plus-portal': 'Portal to Group+ Instances',
  'group-instance-plus-portal-unlocked': 'Unlocked Portal to Group+ Instances',
  'group-instance-join': 'Join Group Instances',
  'group-instance-bypass-avatar-performance': 'Bypass Avatar Performance Requirements',
}

/** A permission from any platform's list, in the words its own settings use, or plain words. */
function permissionLabel(value: unknown): string {
  if (typeof value !== 'string') return JSON.stringify(value)

  const known = VRCHAT_PERMISSIONS[value] ?? DISCORD_PERMISSIONS[value]
  if (known) return known

  const spelled = fieldName(value)
  return `${spelled.charAt(0).toUpperCase()}${spelled.slice(1)}`
}

/** A value short enough to sit inside a sentence. */
function clipped(text: string): string {
  return text.length > 60 ? `${text.slice(0, 59)}…` : text
}

/** A quoted piece of somebody's own words. Rendered as text and never as markup. */
function Quoted({ value }: { value: string | null }) {
  return value ? <> “{value}”</> : null
}

/** The words "VRChat profile", opening the person at the version this fact recorded. */
function VersionLink({ entry }: { entry: AuditEntry }) {
  return (
    <button
      type="button"
      onClick={() => openPersonVersion(entry.subjectId, entry.id)}
      className="rounded-sm font-medium hover:underline focus-visible:outline-2 focus-visible:outline-ring"
      style={{ display: 'inline' }}
      title="The profile as it stood after this change"
    >
      VRChat profile
    </button>
  )
}

// ── The sentences ─────────────────────────────────────────────────────────────────────────────
//
// One per fact type. Where a clause depends on a payload field Modbot may not have kept, the
// clause is omitted rather than guessed at: a thinner true sentence beats a fuller invented one.

type Sentence = (p: Parts) => React.ReactNode

const SENTENCES: Record<string, Sentence> = {
  // ── VRChat: membership and moderation ───────────────────────────────────────────────────────
  'vrchat.group.member.join': (p) => <>{p.subject} joined the group.</>,
  'vrchat.group.member.leave': (p) => <>{p.subject} left the group.</>,

  'vrchat.group.member.ban': (p) =>
    p.hasActor ? <>{p.actor} banned {p.subject} from the group.</> : <>{p.subject} was banned from the group.</>,

  'vrchat.group.member.unban': (p) =>
    p.hasActor ? <>{p.actor} lifted the ban on {p.subject}.</> : <>The ban on {p.subject} was lifted.</>,

  'vrchat.group.member.remove': (p) =>
    p.hasActor ? <>{p.actor} removed {p.subject} from the group.</> : <>{p.subject} was removed from the group.</>,

  'vrchat.group.role.assign': (p) => (
    <>
      {p.actor} gave {p.subject} {named(p.text('roleName'), 'role')}.
    </>
  ),

  'vrchat.group.role.unassign': (p) => (
    <>
      {p.actor} took {named(p.text('roleName'), 'role')} away from {p.subject}.
    </>
  ),

  'vrchat.group.invite.create': (p) => <>{p.actor} invited {p.subject} to the group.</>,

  'vrchat.group.update': (p) => (
    <>
      {p.actor} changed the group's details<Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),

  'vrchat.group.members.snapshot': (p) => {
    const count = p.entry.data?.['memberCount']
    return (
      <>
        Modbot read the member list for the first time
        {typeof count === 'number' ? <>: {count.toLocaleString()} members</> : null}.
      </>
    )
  },

  'vrchat.group.bans.snapshot': (p) => {
    const count = p.entry.data?.['banCount'] ?? p.entry.data?.['memberCount']
    return (
      <>
        Modbot read the ban list for the first time
        {typeof count === 'number' ? <>: {count.toLocaleString()} people</> : null}.
      </>
    )
  },

  'vrchat.group.role.update': (p) => (
    <>
      {p.actor} changed {named(p.text('roleName'), 'role')}
      <Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),

  'vrchat.group.request.create': (p) => <>{p.subject} asked to join the group.</>,
  'vrchat.group.request.reject': (p) => <>{p.actor} turned down {p.subject}'s request to join.</>,
  'vrchat.group.request.block': (p) => <>{p.actor} blocked {p.subject} from asking to join.</>,

  'vrchat.group.post.create': (p) => (
    <>
      {p.actor} posted<Quoted value={p.text('title')} /> to the group.
    </>
  ),

  'vrchat.group.post.delete': (p) => (
    <>
      {p.actor} deleted the group post<Quoted value={p.text('title')} />.
    </>
  ),

  // ── VRChat: group instances ─────────────────────────────────────────────────────────────────
  'vrchat.group.instance.create': (p) => (
    <>
      {p.actor} opened {p.place ?? 'an instance'}
      {access(p.text('groupAccessType'))}.
    </>
  ),

  'vrchat.group.instance.close': (p) => <>{p.actor} closed {p.place ?? 'an instance'}.</>,

  'vrchat.group.instance.update': (p) => (
    <>
      {p.actor} changed {p.place ?? 'an instance'}
      <Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),

  'vrchat.group.instance.announcement': (p) => (
    <>
      {p.actor} announced<Quoted value={p.text('title')} /> in {p.place ?? 'an instance'}
      {p.text('message') ? <>: {p.text('message')}</> : null}.
    </>
  ),

  // The clause is there only when the kick was recorded with an answer, which needs a moderator's
  // companion to have been reporting that instance at the time. Most groups' kicks carry none, and
  // the sentence is the one it has always been.
  'vrchat.group.instance.kick': (p) => {
    const howLong = timeInInstance(p.entry.data)

    return (
      <>
        {p.actor} kicked {p.subject} out of {p.place ?? 'an instance'}
        {howLong ? ` ${howLong}` : ''}.
      </>
    )
  },

  'vrchat.group.instance.warn': (p) => (
    <>
      {p.actor} warned {p.subject} in {p.place ?? 'an instance'}.
    </>
  ),

  'vrchat.group.calendar-event.create': (p) => (
    <>
      {p.actor} created the calendar entry<Quoted value={p.text('title')} />.
    </>
  ),

  'vrchat.group.calendar-event.delete': (p) => (
    <>
      {p.actor} deleted the calendar entry<Quoted value={p.text('title')} />.
    </>
  ),

  'vrchat.group.calendar-event.series.update': (p) => (
    <>
      {p.actor} changed a repeating calendar entry<Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),

  'vrchat.group.calendar-event.series.delete': (p) => <>{p.actor} deleted a repeating calendar entry.</>,

  // ── VRChat: profiles ────────────────────────────────────────────────────────────────────────
  // "VRChat profile" opens the person on their History tab at this very version: the fact is
  // the snapshot, replayed by the server from the facts around it.
  'vrchat.user.profile.first-seen': (p) => (
    <>
      Modbot recorded {p.subject}'s <VersionLink entry={p.entry} /> for the first time.
    </>
  ),

  'vrchat.user.profile.changed': (p) => (
    <>
      {p.subject}'s <VersionLink entry={p.entry} /> changed<Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),

  'vrchat.user.profile.not-found': (p) => <>VRChat no longer has an account for {p.subject}.</>,
  'vrchat.user.age-verified': (p) => <>VRChat showed {p.subject} as 18+ verified.</>,

  'modbot.user-profile.age-flag.set': (p) => (
    <>
      {p.actor} marked {p.subject} as 18+ verified
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),

  'modbot.user-profile.age-flag.cleared': (p) => (
    <>
      {p.actor} cleared the 18+ verified mark on {p.subject}
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),

  // ── VRChat: presence, from a moderator's companion ─────────────────────────────────────
  'vrchat.instance.join': (p) => <>{p.subject} joined {p.place ?? 'an instance'}.</>,
  'vrchat.instance.leave': (p) => <>{p.subject} left {p.place ?? 'an instance'}.</>,

  'vrchat.instance.presence': (p) => (
    <>
      {p.subject} was already in {p.place ?? 'an instance'} when a moderator's client arrived.
    </>
  ),

  // VRChat's log carries an avatar's display name and never an `avtr_…` id, so the name is all
  // there is to show and two avatars called the same thing cannot be told apart. A row recorded
  // before the name was kept, or one whose log line could not be split against the roster, still
  // reads the short way.
  'vrchat.avatar.change': (p) => {
    const avatar = avatarWorn(p.entry.data)

    return avatar ? (
      <>
        {p.subject} switched to the avatar<Quoted value={avatar} />.
      </>
    ) : (
      <>{p.subject} changed avatar.</>
    )
  },

  // ── Discord ─────────────────────────────────────────────────────────────────────────────────
  'discord.member.join': (p) => <>{p.subject} joined the Discord server.</>,
  'discord.member.leave': (p) => <>{p.subject} left the Discord server.</>,
  'discord.voice.join': (p) => <>{p.subject} joined {voice(p.text('channelName'))}.</>,
  'discord.voice.leave': (p) => <>{p.subject} left {voice(p.text('channelName'))}.</>,

  'discord.voice.move': (p) => {
    const to = p.text('channelName')
    const from = p.text('fromName')

    return from && to ? (
      <>
        {p.subject} moved from {from} to {to} in Discord voice.
      </>
    ) : (
      <>{p.subject} moved to {voice(to)}.</>
    )
  },

  'discord.members.snapshot': (p) => {
    const count = p.entry.data?.['count']
    return (
      <>
        Modbot read the Discord server's member list for the first time
        {typeof count === 'number' ? `: ${count.toLocaleString()} members` : ''}.
      </>
    )
  },

  'discord.member.ban': (p) => (
    <>
      {p.hasActor ? <>{p.actor} banned {p.subject} from the Discord server</> : <>{p.subject} was banned from the Discord server</>}
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),
  'discord.member.unban': (p) => (
    <>{p.hasActor ? <>{p.actor} unbanned {p.subject} on Discord</> : <>{p.subject} was unbanned on Discord</>}.</>
  ),
  'discord.member.kick': (p) => (
    <>
      {p.actor} kicked {p.subject} from the Discord server
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),
  'discord.member.timeout': (p) => (
    <>
      {p.hasActor ? <>{p.actor} timed out {p.subject} on Discord</> : <>{p.subject} was timed out on Discord</>}
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),
  'discord.member.timeout.remove': (p) => (
    <>{p.hasActor ? <>{p.actor} took {p.subject}'s Discord timeout off</> : <>{p.subject}'s Discord timeout was taken off</>}.</>
  ),
  'discord.member.nickname': (p) => (
    <>
      {p.subject} changed their Discord nickname
      {p.text('new') ? <> to {p.text('new')}</> : null}.
    </>
  ),
  'discord.message.remove': (p) => (
    <>
      {p.actor} removed {howMany(p, 'some')} of {p.subject}'s Discord messages.
    </>
  ),
  'discord.message.bulk-remove': (p) => (
    <>
      {p.actor} removed {howMany(p, 'many')} Discord messages at once.
    </>
  ),
  'discord.channel.create': (p) => <>{p.actor} created {named(p.text('name'), 'Discord channel')}.</>,
  'discord.channel.update': (p) => (
    <>
      {p.actor} changed {named(p.text('name'), 'Discord channel')}
      <Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),
  'discord.channel.delete': (p) => <>{p.actor} deleted {named(p.text('name'), 'Discord channel')}.</>,
  'discord.role.create': (p) => <>{p.actor} created {named(p.text('name'), 'Discord role')}.</>,
  'discord.role.update': (p) => {
    const given = permissions(p.entry.data?.['permissionsGiven'])
    const taken = permissions(p.entry.data?.['permissionsTaken'])
    return (
      <>
        {p.actor} changed {named(p.text('name'), 'Discord role')}
        <Changed changed={p.changed} />.
        <ChangedLists changed={p.changed} />
        <Bullets heading="Gave it" items={given} />
        <Bullets heading="Took away" items={taken} />
      </>
    )
  },
  'discord.role.delete': (p) => <>{p.actor} deleted {named(p.text('name'), 'Discord role')}.</>,

  'discord.role.assign': (p) => <>{p.subject} was given {named(p.text('roleName'), 'Discord role')}.</>,
  'discord.role.unassign': (p) => <>{p.subject} lost {named(p.text('roleName'), 'Discord role')}.</>,

  'modbot.discord.command': (p) => (
    <>
      {p.subject} used the Discord command {p.text('command') ? `/${p.text('command')}` : 'a command'}.
    </>
  ),

  'modbot.discord.posted': (p) => {
    const count = p.entry.data?.['count']
    return (
      <>
        Modbot posted {typeof count === 'number' ? count.toLocaleString() : 'some'} moderation events to
        the Discord log channel.
      </>
    )
  },

  // ── Modbot accounts ─────────────────────────────────────────────────────────────────────────
  'modbot.chat.lookup': (p) => {
    const people = p.entry.data?.['people']
    const others = Array.isArray(people) ? people.length - 1 : 0

    // Through the MCP server, the person's own AI app ran the tool; the payload names it.
    const via = p.text('via') === 'mcp' ? `through ${p.text('client') ?? 'an AI app'}` : 'in chat'

    return (
      <>
        {p.actor} asked about {p.subject}
        {others > 0 ? <> and {others === 1 ? '1 other person' : `${others} other people`}</> : null} {via}.
      </>
    )
  },

  'modbot.mcp.connect': (p) => <>{p.actor} connected {p.text('client') ?? 'an AI app'} to Modbot.</>,
  'modbot.mcp.disconnect': (p) => <>{p.actor} disconnected {p.text('client') ?? 'an AI app'} from Modbot.</>,

  'modbot.user.login': (p) => <>{p.subject} signed in to Modbot.</>,

  'modbot.user.login.failed': (p) => (
    <>
      A sign-in as {p.text('username') ?? 'an unknown account'} failed
      {p.text('address') ? <> from {p.text('address')}</> : null}.
    </>
  ),

  'modbot.user.password.change': (p) => <>{p.subject} changed their Modbot password.</>,

  'modbot.user.username.change': (p) => (
    <>
      {p.subject} changed their Modbot username
      {p.text('from') ? <> from {p.text('from')} to {p.text('to')}</> : null}.
    </>
  ),

  'modbot.user.contact.change': (p) => <>{p.subject}'s contact details changed.</>,
  'modbot.user.vrchat.link': (p) => <>{p.subject} proved which VRChat account is theirs.</>,
  'modbot.user.create': (p) => <>{p.actor} created the Modbot account {p.subject}.</>,

  'modbot.user.invite.create': (p) => (
    <>
      {p.actor} made an invite link
      {p.text('roles') ? <> for {p.text('roles')}</> : null}.
    </>
  ),

  'modbot.user.invite.use': () => <>An invite link was used to create a Modbot account.</>,
  'modbot.user.invite.revoke': (p) => <>{p.actor} took back an invite link.</>,
  'modbot.user.disable': (p) => <>{p.actor} disabled the Modbot account {p.subject}.</>,
  'modbot.user.enable': (p) => <>{p.actor} enabled the Modbot account {p.subject}.</>,

  'modbot.user.roles.change': (p) => (
    <>
      {p.actor} changed {p.subject}'s roles
      {p.text('before') !== null ? <>, from {p.text('before') || 'none'} to {p.text('after') || 'none'}</> : null}.
    </>
  ),

  'modbot.user.password.reset.create': (p) => <>{p.actor} made a password reset link for {p.subject}.</>,
  'modbot.user.password.reset.use': (p) => <>{p.subject} used a password reset link.</>,
  'modbot.user.sign-out-everywhere': (p) => <>{p.subject} was signed out of Modbot everywhere.</>,

  'modbot.role.create': (p) => <>{p.actor} created {named(p.text('name'), 'Modbot role')}.</>,
  'modbot.role.change': (p) => (
    <>
      {p.actor} changed {named(p.text('name'), 'Modbot role')}
      <Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),
  'modbot.role.delete': (p) => <>{p.actor} deleted {named(p.text('name'), 'Modbot role')}.</>,

  'modbot.apikey.create': (p) => <>{p.actor} created an API key.</>,
  'modbot.apikey.revoke': (p) => <>{p.actor} revoked an API key.</>,

  'modbot.settings.change': (p) => (
    <>
      {p.actor} changed {p.text('setting') ? `the ${fieldName(p.text('setting')!)} setting` : "Modbot's settings"}
      {p.text('before') !== null || p.text('after') !== null ? (
        <>, from {p.text('before') || 'nothing'} to {p.text('after') || 'nothing'}</>
      ) : null}
      <Changed changed={p.changed} />.
      <ChangedLists changed={p.changed} />
    </>
  ),

  // ── Reviews of a moderator's pattern ────────────────────────────────────────────────────────
  'modbot.review.opened': (p) => (
    <>
      Modbot opened a review of {p.subject}
      {p.entry.description ? <>: {p.entry.description}</> : null}
    </>
  ),

  'modbot.review.closed': (p) => (
    <>
      {p.actor} closed a review of {p.subject}
      {p.text('note') ? <>: {p.text('note')}</> : null}.
    </>
  ),

  // ── Case files ──────────────────────────────────────────────────────────────────────────────
  'modbot.report.created': (p) => (
    <>
      {p.actor} wrote a case file for {p.subject}
      {reasons(p) ? <>: {reasons(p)}</> : null}.
    </>
  ),

  'modbot.report.updated': (p) => <>{p.actor} edited the case file for {p.subject}.</>,

  'modbot.report.withdrawn': (p) => (
    <>
      {p.actor} withdrew the case file for {p.subject}
      {p.text('note') ? <>: {p.text('note')}</> : null}.
    </>
  ),

  'modbot.report.snapshot.recaptured': (p) => (
    <>The profile kept on {p.subject}'s case file was taken again from a fresher one.</>
  ),

  'modbot.ban-reasons.change': (p) => <>{p.actor} changed the list of ban reasons.</>,

  // ── Moderation done from Modbot ─────────────────────────────────────────────────────────────
  //
  // Worded so these read differently from VRChat's own entries for the same moment. VRChat's say
  // Modbot's account did it; these say who decided to.
  'modbot.action.kick': (p) => (
    <>
      {p.actor} kicked {p.subject} from the group
      {reasons(p) ? <>: {reasons(p)}</> : null}.
    </>
  ),

  'modbot.action.ban': (p) => (
    <>
      {p.actor} banned {p.subject} from the group
      {reasons(p) ? <>: {reasons(p)}</> : null}.
    </>
  ),

  'modbot.action.unban': (p) => (
    <>
      {p.actor} unbanned {p.subject}
      {reasons(p) ? <>: {reasons(p)}</> : null}.
    </>
  ),

  'modbot.action.failed': (p) => (
    <>
      {p.actor} tried to {p.text('action') ?? 'act on'} {p.subject} and VRChat refused
      {p.text('vrchatSaid') ? <>: {p.text('vrchatSaid')}</> : null}.
    </>
  ),

  // ── Notes ───────────────────────────────────────────────────────────────────────────────────
  //
  // The note itself is in the sentence, because a row that said only "wrote a note" would make a
  // reader open something to learn what the whole entry is. `text` is what Modbot writes; an
  // imported note carries whatever its file held, so `description` stands in for it.
  'modbot.note.add': (p) => (
    <>
      {p.actor} wrote a note about {p.subject}
      {p.text('text') ?? p.text('description') ? <>: {p.text('text') ?? p.text('description')}</> : null}.
    </>
  ),

  'modbot.note.take-back': (p) => (
    <>
      {p.actor} took back a note about {p.subject}
      {p.text('text') ? <>: {p.text('text')}</> : null}.
    </>
  ),

  // ── Evidence ────────────────────────────────────────────────────────────────────────────────
  'modbot.evidence.attach': (p) => (
    <>
      {p.actor} attached {p.text('fileName') ?? 'a file'} to a case file.
    </>
  ),

  'modbot.evidence.access': (p) => (
    <>
      {p.actor} opened {p.text('fileName') ?? 'a piece of evidence'}.
    </>
  ),

  'modbot.evidence.destroy': (p) => (
    <>
      {p.actor} destroyed {p.text('fileName') ?? 'a piece of evidence'}.
    </>
  ),

  // ── Modbot's own workings ───────────────────────────────────────────────────────────────────
  'modbot.sync.failed': (p) => (
    <>
      A sync failed{p.text('sync') ? <> ({p.text('sync')})</> : null}
      {p.text('error') ? <>: {p.text('error')}</> : null}.
    </>
  ),

  'modbot.ratelimit.coldstop': (p) => (
    <>
      VRChat rate limited Modbot, so {p.text('bucket') ? `the ${p.text('bucket')} lane` : 'a lane'} stopped
      until it clears.
    </>
  ),

  'modbot.waf.blocked': () => <>Cloudflare blocked one of Modbot's requests to VRChat.</>,

  'modbot.migration.applied': (p) => (
    <>
      A database change was applied{p.text('name') ? <>: {p.text('name')}</> : null}.
    </>
  ),

  'modbot.retention.pruned': (p) => (
    <>
      Old facts were deleted under the retention settings
      {p.text('dropped') ? <>: {p.text('dropped')}</> : null}.
    </>
  ),

  'modbot.ai.limit.reached': (p) => <>{p.text('message') ?? 'An AI spend limit was reached.'}</>,

  'modbot.partition.created': (p) => (
    <>
      A new month of the fact log was created{p.text('name') ? <>: {p.text('name')}</> : null}.
    </>
  ),

  'modbot.user.purged': (p) => {
    const facts = p.entry.data?.['facts']
    return (
      <>
        Every fact about one person was erased on request
        {typeof facts === 'number' ? <>, {facts.toLocaleString()} of them</> : null}.
      </>
    )
  },

  // ── Join requests answered from Modbot ─────────────────────────────────────────────────────
  'modbot.action.request.approve': (p) => (
    <>
      {p.actor} let {p.subject} into the group
      {(reasons(p) ?? p.text('note')) ? <>: {reasons(p) ?? p.text('note')}</> : null}.
    </>
  ),

  'modbot.action.request.reject': (p) => (
    <>
      {p.actor} turned down {p.subject}'s request to join the group
      {(reasons(p) ?? p.text('note')) ? <>: {reasons(p) ?? p.text('note')}</> : null}.
    </>
  ),

  // ── Calendar ────────────────────────────────────────────────────────────────────────────────
  //
  // The event is named by its title in quotes: its id means nothing to a moderator, and the title
  // is kept in every one of these payloads.
  'modbot.calendar.event.create': (p) => {
    const starts = when(p.text('startsAt'))
    return (
      <>
        {p.actor} planned the event<Quoted value={p.text('title')} />
        {starts ? <> for {starts}</> : null}
        {repeats(p.entry.data)}
        {p.text('state') === 'draft' ? ' as a draft' : null}.
      </>
    )
  },

  'modbot.calendar.event.change': (p) => {
    const before = record(p.entry.data?.['before'])
    const after = record(p.entry.data?.['after'])
    const published = before?.['state'] === 'draft' && after?.['state'] !== 'draft'
    const fields = differences(before, after, CALENDAR_FIELDS)

    // Before 2026-09-25 the description, pictures, languages, platforms, tags, category, visibility
    // and notifying were not kept, so an edit to only those left no difference to show.
    const unrecorded = fields.length === 0 && !published && before !== null && !('description' in before)

    return (
      <>
        {p.actor} {published ? 'published the draft event' : 'changed the event'}
        <Quoted value={p.text('title')} />
        {fields.length > 0 ? <>: {fields.join('; ')}</> : null}
        {unrecorded ? ', in a part Modbot did not record then' : null}.
      </>
    )
  },

  'modbot.calendar.event.cancel': (p) => (
    <>
      {p.actor} cancelled the event<Quoted value={p.text('title')} />.
    </>
  ),

  'modbot.calendar.event.delete': (p) => (
    <>
      {p.actor} deleted the event<Quoted value={p.text('title')} />.
    </>
  ),

  'modbot.calendar.event.open': (p) => {
    const starts = when(p.text('occurrenceStartsAt'))
    return (
      <>
        The event<Quoted value={p.text('title')} /> started{starts ? <> ({starts})</> : null}.
      </>
    )
  },

  'modbot.calendar.event.finish': (p) => (
    <>
      The event<Quoted value={p.text('title')} /> is over.
    </>
  ),

  'modbot.calendar.instance.open': (p) => (
    <>
      Modbot opened {p.place ?? 'an instance'} for the event<Quoted value={p.text('title')} />.
    </>
  ),

  'modbot.calendar.instance.fail': (p) => (
    <>
      Modbot could not open an instance{p.world ? <> of {p.world}</> : null} for the event
      <Quoted value={p.text('title')} />
      {p.text('error') ? <>: {p.text('error')}</> : null}.
    </>
  ),

  // `check`: a create that got no answer was not tried again, because Modbot could not read
  // VRChat's calendar to see whether the first try had made the event after all.
  'modbot.calendar.publish.fail': (p) =>
    p.text('action') === 'check' ? (
      <>
        Modbot held off adding<Quoted value={p.text('title')} /> to VRChat's calendar again, because it could not
        check whether the first try went through{p.text('error') ? <>: {p.text('error')}</> : null}.
      </>
    ) : (
      <>
        {publishFailure(p.text('place'), p.text('action'), <Quoted value={p.text('title')} />)}
        {p.text('error') ? <>: {p.text('error')}</> : null}.
      </>
    ),

  'modbot.calendar.feed.regenerate': (p) =>
    p.entry.data?.['replaced'] === true ? (
      <>{p.actor} made a new calendar feed link, and the old one stopped working.</>
    ) : (
      <>{p.actor} made the calendar feed link.</>
    ),

  // ── Giveaways ───────────────────────────────────────────────────────────────────────────────
  'modbot.giveaway.create': (p) => (
    <>
      {p.actor} set up the giveaway<Quoted value={p.text('name')} />
      {p.text('prize') ? <> for {p.text('prize')}</> : null}
      {p.text('state') === 'draft' ? ' as a draft' : null}.
    </>
  ),

  'modbot.giveaway.change': (p) => {
    const before = record(p.entry.data?.['before'])
    const after = record(p.entry.data?.['after'])
    const published = before?.['state'] === 'draft' && after?.['state'] !== 'draft'
    const fields = differences(before, after, GIVEAWAY_FIELDS)

    return (
      <>
        {p.actor} {published ? 'published the draft giveaway' : 'changed the giveaway'}
        <Quoted value={p.text('name')} />
        {fields.length > 0 ? <>: {fields.join('; ')}</> : null}.
      </>
    )
  },

  'modbot.giveaway.open': (p) => (
    <>
      {p.actor} opened entries for the giveaway<Quoted value={p.text('name')} />.
    </>
  ),

  'modbot.giveaway.close': (p) =>
    p.hasActor ? (
      <>
        {p.actor} closed entries for the giveaway<Quoted value={p.text('name')} />.
      </>
    ) : (
      <>
        Entries for the giveaway<Quoted value={p.text('name')} /> closed.
      </>
    ),

  'modbot.giveaway.enter': (p) =>
    p.entry.data?.['qualified'] === false ? (
      <>
        {p.subject} tried to enter the giveaway<Quoted value={p.text('name')} /> but could not
        {p.text('because') ? <>: {p.text('because')}</> : null}.
      </>
    ) : (
      <>
        {p.subject} entered the giveaway<Quoted value={p.text('name')} />.
      </>
    ),

  'modbot.giveaway.withdraw': (p) => (
    <>
      {p.subject} left the giveaway<Quoted value={p.text('name')} />.
    </>
  ),

  'modbot.giveaway.draw': (p) => {
    const draw = p.entry.data?.['drawNumber']
    const verb = typeof draw === 'number' && draw > 1 ? 'drew again for' : 'drew'
    const inDraw = p.entry.data?.['inDraw']
    const names = winnerNames(p.entry.data?.['winners'])

    return (
      <>
        {p.hasActor ? p.actor : 'Modbot'} {verb} the giveaway
        <Quoted value={p.text('name')} />
        {names ? <>: {names} won</> : null}
        {typeof inDraw === 'number' ? <>, out of {inDraw.toLocaleString()} in the draw</> : null}.
      </>
    )
  },

  'modbot.giveaway.cancel': (p) => (
    <>
      {p.actor} cancelled the giveaway<Quoted value={p.text('name')} />.
    </>
  ),

  'modbot.giveaway.delete': (p) => (
    <>
      {p.actor} deleted the giveaway<Quoted value={p.text('name')} />.
    </>
  ),

  'modbot.giveaway.winner.announce': (p) => (
    <>
      Modbot named {p.entry.data?.['winners'] === 1 ? 'the winner' : 'the winners'} of the giveaway
      <Quoted value={p.text('name')} /> in the Discord channel.
    </>
  ),

  // Written both when the Discord post fails and when a timed draw cannot be made; the payload
  // does not say which, so the sentence does not either.
  'modbot.giveaway.publish.fail': (p) => (
    <>
      The giveaway<Quoted value={p.text('name')} /> ran into a problem
      {p.text('error') ? <>: {p.text('error')}</> : null}.
    </>
  ),

  // ── Webhooks ────────────────────────────────────────────────────────────────────────────────
  'modbot.webhook.create': (p) => (
    <>
      {p.actor} created the webhook<Quoted value={p.text('name')} />
      {p.text('url') ? <> sending to {p.text('url')}</> : null}.
    </>
  ),

  'modbot.webhook.change': (p) => {
    const before = record(p.entry.data?.['before'])
    const after = record(p.entry.data?.['after'])
    const name = typeof after?.['name'] === 'string' ? after['name'] : null
    const fields = differences(before, after, WEBHOOK_FIELDS)

    if (fields.length === 0 && before?.['enabled'] !== after?.['enabled'])
      return (
        <>
          {p.actor} turned the webhook<Quoted value={name} /> {after?.['enabled'] ? 'on' : 'off'}.
        </>
      )

    return (
      <>
        {p.actor} changed the webhook<Quoted value={name} />
        {fields.length > 0 ? <>: {fields.join('; ')}</> : null}.
      </>
    )
  },

  'modbot.webhook.secret.change': (p) => (
    <>
      {p.actor} made a new secret for the webhook<Quoted value={p.text('name')} />.
    </>
  ),

  'modbot.webhook.delete': (p) => (
    <>
      {p.actor} deleted the webhook<Quoted value={p.text('name')} />.
    </>
  ),

  'modbot.webhook.disable': (p) => (
    <>
      Modbot turned off the webhook<Quoted value={p.text('name')} />
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),

  // ── AutoMod ─────────────────────────────────────────────────────────────────────────────────
  'modbot.ai-moderation.flag': (p) => (
    <>
      {rule(p.text('ruleKind'), p.text('ruleName'))} flagged {p.subject}'s {targetWord(p.text('target'))}
      {p.entry.data?.['trial'] === true ? ' while it was being tested' : null}
      {p.text('matched') ? (
        <>
          :<Quoted value={p.text('matched')} />
        </>
      ) : null}
      .
    </>
  ),

  'modbot.ai-moderation.flag.confirm': (p) => (
    <>
      {p.actor} agreed with {rule(p.text('ruleKind'), p.text('ruleName'), false)} flagging {p.subject}.
    </>
  ),

  'modbot.ai-moderation.flag.dismiss': (p) => (
    <>
      {p.actor} dismissed a flag on {p.subject} from {rule(p.text('ruleKind'), p.text('ruleName'), false)}.
    </>
  ),

  'modbot.ai-moderation.message-delete': (p) => (
    <AutoModAction
      p={p}
      did={<>deleted {p.subject}'s Discord message</>}
      tried={<>delete {p.subject}'s Discord message</>}
    />
  ),

  'modbot.ai-moderation.timeout': (p) => {
    const minutes = p.entry.data?.['minutes']
    const span = typeof minutes === 'number' ? <> for {duration(minutes * 60)}</> : null
    return (
      <AutoModAction
        p={p}
        did={
          <>
            timed out {p.subject} on Discord{span}
          </>
        }
        tried={
          <>
            time out {p.subject} on Discord{span}
          </>
        }
      />
    )
  },

  'modbot.ai-moderation.group-ban': (p) => (
    <AutoModAction p={p} did={<>banned {p.subject} from the group</>} tried={<>ban {p.subject} from the group</>} />
  ),

  'modbot.ai-moderation.group-remove': (p) => (
    <AutoModAction
      p={p}
      did={<>removed {p.subject} from the group</>}
      tried={<>remove {p.subject} from the group</>}
    />
  ),

  'modbot.ai-moderation.rule.change': (p) => <>{ruleChange(p)}.</>,

  'modbot.ai-moderation.rule.pause': (p) => (
    <>
      {rule(p.text('ruleKind'), p.text('ruleName'))} paused itself
      {p.text('reason') ? <>: {p.text('reason')}</> : null}.
    </>
  ),

  'modbot.ai.acknowledge': (p) => <>{p.actor} confirmed what member text Modbot may send to the AI provider.</>,

  // ── Bans and roles copied between VRChat and Discord ────────────────────────────────────────
  'modbot.copy.ban': (p) =>
    p.text('direction') === 'to-vrchat' ? (
      <>Modbot banned {p.subject} from the group, because they were banned on Discord.</>
    ) : (
      <>Modbot banned {p.subject} from the Discord server, because they were banned from the group.</>
    ),

  'modbot.copy.unban': (p) =>
    p.text('direction') === 'to-vrchat' ? (
      <>Modbot lifted the ban on {p.subject} in the group, because it was lifted on Discord.</>
    ) : (
      <>Modbot lifted the ban on {p.subject} on the Discord server, because it was lifted in the group.</>
    ),

  'modbot.copy.remove': (p) => (
    <>Modbot removed {p.subject} from the Discord server, because they were banned from the group.</>
  ),

  'modbot.copy.role.give': (p) =>
    p.entry.subjectPlatform === 'Discord' ? (
      <>
        Modbot gave {p.subject} {named(p.text('roleName'), 'Discord role')}, to match their role in the group.
      </>
    ) : (
      <>
        Modbot gave {p.subject} {named(p.text('roleName'), 'group role')}, to match their role on Discord.
      </>
    ),

  'modbot.copy.role.take': (p) =>
    p.entry.subjectPlatform === 'Discord' ? (
      <>
        Modbot took {named(p.text('roleName'), 'Discord role')} away from {p.subject}, to match their roles in the
        group.
      </>
    ) : (
      <>
        Modbot took {named(p.text('roleName'), 'group role')} away from {p.subject}, to match their roles on
        Discord.
      </>
    ),

  // The ban copy says which kind it was; the role copy carries the role instead.
  'modbot.copy.failed': (p) => (
    <>
      {p.text('kind') ? (
        <>
          Modbot could not copy {p.text('kind') === 'unban' ? 'the unban of' : 'the ban on'} {p.subject} to{' '}
          {p.text('direction') === 'to-vrchat' ? 'the group' : 'the Discord server'}
        </>
      ) : (
        <>
          Modbot could not change {p.subject}'s {p.text('roleName') ? <>role {p.text('roleName')}</> : 'paired role'}{' '}
          to match the other side
        </>
      )}
      {p.text('error') ? <>: {p.text('error')}</> : null}.
    </>
  ),

  'modbot.copy.disagree': (p) => (
    <>
      {p.subject} has a paired role only {p.text('heldIn') === 'discord' ? 'on Discord' : 'in the group'}, and
      neither side decides that pair, so Modbot changed nothing.
    </>
  ),

  // ── Invites Modbot sent on its own ──────────────────────────────────────────────────────────
  'modbot.group.auto-invite': (p) => {
    const minutes = p.entry.data?.['minutesInInstance']
    const atLeast = p.entry.data?.['seenArriving'] === false ? 'at least ' : ''
    return (
      <>
        Modbot invited {p.subject} to the group
        {typeof minutes === 'number' ? (
          <>
            {' '}
            after {atLeast}
            {duration(minutes * 60)}
          </>
        ) : null}
        {p.place ? <> in {p.place}</> : null}.
      </>
    )
  },

  'modbot.group.auto-invite.failed': (p) => (
    <>
      Modbot could not invite {p.subject} to the group
      {p.text('problem') ? <>: {p.text('problem')}</> : null}.
    </>
  ),

  // ── Everything else Modbot does ─────────────────────────────────────────────────────────────
  'modbot.import.done': (p) => {
    const file = p.text('fileName') ?? 'a file'
    const from = p.text('source') ? <> from {p.text('source')}</> : null

    if (p.text('status') === 'Failed')
      return (
        <>
          {p.actor}'s import of {file}
          {from} failed.
        </>
      )

    const counts = importCounts(p.entry.data)
    return (
      <>
        {p.actor} imported {file}
        {from}
        {counts ? <>: {counts}</> : null}.
      </>
    )
  },

  'modbot.insight.alert': (p) => {
    const now = p.entry.data?.['now']
    const normal = p.entry.data?.['normal']
    const numbers = typeof now === 'number' && typeof normal === 'number'

    return (
      <>
        {p.text('label') ?? 'Something Modbot watches'} was unusually {numbers && now < normal ? 'low' : 'high'}
        {p.text('where') ? <> in {p.text('where')}</> : null}
        {numbers ? (
          <>
            : {now.toLocaleString()}, where {normal.toLocaleString()} is normal
          </>
        ) : null}
        .{p.text('text') ? <> {p.text('text')}</> : null}
      </>
    )
  },

  'modbot.user.delete': (p) => (
    <>
      {p.actor} deleted the Modbot account {p.text('was') ?? p.subject}.
    </>
  ),

  'modbot.user.updates.subscribe': (p) => <>{p.subject} asked Modbot Cloud for news about updates.</>,

  // ── Something Modbot has no name for ────────────────────────────────────────────────────────
  'modbot.unrecognised': (p) => <Unrecognised parts={p} />,
}

/**
 * Sentences looked up by the source's own word for an event.
 *
 * Empty today, and the point of it is that it can stop being. A fact recorded before Modbot knew a
 * word is stored as `modbot.unrecognised` with the word in `typeRaw`; putting the word here makes
 * every such row, however old, read properly the next time somebody opens the page. No job runs,
 * and no stored fact is touched.
 */
const RAW: Record<string, Sentence> = {}

/**
 * An event Modbot has no name for yet, said plainly, with what VRChat actually sent underneath.
 *
 * The row is not a failure and must not read like one: the event happened, Modbot recorded all of
 * it, and only the wording is missing. The raw event is shown so a moderator can read what
 * happened without waiting for anybody to add a sentence.
 */
function Unrecognised({ parts: p }: { parts: Parts }) {
  const eventType = p.text('eventType') ?? p.entry.typeRaw
  const payload = p.entry.data?.['auditData'] ?? p.entry.data

  return (
    <>
      <span>Modbot doesn't recognise this event yet.</span>{' '}
      {p.hasActor ? <>{p.actor} did it to {p.subject}. </> : <>It is about {p.subject}. </>}
      {p.entry.description && <span className="text-muted-foreground">{p.entry.description} </span>}

      <details className="group mt-1">
        <summary className="flex w-fit max-w-full cursor-pointer list-none items-center gap-1 rounded-sm text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring [&::-webkit-details-marker]:hidden">
          <ChevronRight
            className="size-3.5 shrink-0 transition-transform group-open:rotate-90 motion-reduce:transition-none"
            aria-hidden
          />
          <span className="min-w-0">
            VRChat called it{' '}
            {eventType ? <span className="font-mono">{eventType}</span> : 'nothing at all'}. Show what it sent
          </span>
        </summary>
        <JsonView className="mt-1" title="Event" value={payload ?? {}} />
      </details>
    </>
  )
}

/**
 * A type with no sentence written for it.
 *
 * Says what it can from the columns every fact has, and prints the type verbatim rather than a
 * friendly guess: an invented description of something nobody has described is worse than an
 * ugly true one, and the ugliness is what gets a sentence written.
 */
function fallback(p: Parts): React.ReactNode {
  return (
    <>
      {p.hasActor ? <>{p.actor} · </> : null}
      <span className="font-mono">{p.entry.type}</span> · {p.subject}
      {p.place ? <> in {p.place}</> : null}
      {p.entry.description ? <>. {p.entry.description}</> : null}
    </>
  )
}

/** How open an instance is, in a word a member would use. */
function access(groupAccessType: string | null): React.ReactNode {
  if (!groupAccessType) return null

  // A word this build has not seen is shown as VRChat wrote it. It is still the real answer.
  const words: Record<string, string> = {
    members: 'group members only',
    plus: 'members and their friends',
    public: 'anyone',
  }

  return <>, open to {words[groupAccessType] ?? groupAccessType}</>
}

/** The reason labels on a case file fact, as one phrase. */
function reasons(p: Parts): string | null {
  const labels = p.entry.data?.['reasonLabels']
  if (!Array.isArray(labels) || labels.length === 0) return null

  return labels.filter((l): l is string => typeof l === 'string').join(', ') || null
}

// ── Helpers for Modbot's own sentences ────────────────────────────────────────────────────────

/** A payload value that is an object, or null. */
function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}

/** A moment from a payload, in the reader's own time: "Fri 25 Sep, 1:00 PM". */
function when(iso: string | null): string | null {
  if (!iso) return null
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return null

  return date.toLocaleString(undefined, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: 'numeric',
    minute: '2-digit',
  })
}

const DAY_NAMES: Record<string, string> = {
  MO: 'Monday',
  TU: 'Tuesday',
  WE: 'Wednesday',
  TH: 'Thursday',
  FR: 'Friday',
  SA: 'Saturday',
  SU: 'Sunday',
}

/** ", repeating every Monday and Friday" for a repeating event; nothing for a one-off. */
function repeats(data: Record<string, unknown> | null): React.ReactNode {
  return data && data['repeat'] && data['repeat'] !== 'none' ? `, repeating ${repeatWords(data)}` : null
}

/** "every Monday and Friday", "every day", or "not at all". */
function repeatWords(data: Record<string, unknown>): string {
  const repeat = data['repeat']
  if (repeat === 'daily') return 'every day'
  if (repeat === 'monthly') return 'every month'
  if (repeat !== 'weekly') return 'not at all'

  const days = typeof data['repeatDays'] === 'string' ? data['repeatDays'] : ''
  const named = days
    .split(',')
    .map((d) => DAY_NAMES[d.trim()])
    .filter((d): d is string => !!d)

  return named.length > 0 ? `every ${list(named)}` : 'every week'
}

/** "a, b and c". */
function list(items: string[]): string {
  if (items.length <= 1) return items.join('')
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`
}

/**
 * How one field of a thing is said when it changes.
 *
 * A bare string names the field and nothing more, for values no sentence can hold (a description,
 * a rule tree). `show` says the value, and the change reads "who can join from group members only
 * to anyone". `toggle` is for on/off fields. `say` is for fields whose change needs its own words.
 */
type Say =
  | string
  | { word: string; show: (value: unknown) => string }
  | { toggle: string }
  | { say: (before: unknown, after: unknown, whole: Record<string, unknown>) => string }

/**
 * What changed between two copies of a thing, each as a short phrase with its values.
 *
 * Fields not listed are left out on purpose: `state` is said by the sentence itself, and
 * `seedPromise` is not something anybody edits. Two fields that change together, like a start
 * and an end, say the same phrase and are said once.
 */
function differences(
  before: Record<string, unknown> | null,
  after: Record<string, unknown> | null,
  fields: Record<string, Say>,
): string[] {
  if (!before || !after) return []

  const phrases = Object.entries(fields)
    .filter(([key]) => JSON.stringify(before[key] ?? null) !== JSON.stringify(after[key] ?? null))
    .map(([key, how]) => {
      const was = before[key]
      const now = after[key]

      if (typeof how === 'string') return how
      if ('toggle' in how) return `${now ? 'turned on' : 'turned off'} ${how.toggle}`
      if ('say' in how) return how.say(was, now, after)
      return `${how.word} from ${clipped(how.show(was))} to ${clipped(how.show(now))}`
    })

  return [...new Set(phrases)]
}

/** A value as plain text, or "none". */
const plain = (value: unknown): string =>
  value === null || value === undefined || value === '' ? 'none' : String(value)

/** A title or name, in quotes. */
const quoted = (value: unknown): string => (typeof value === 'string' && value ? `“${value}”` : 'none')

/** A moment, in the reader's own time. */
const moment = (value: unknown): string => (typeof value === 'string' ? (when(value) ?? value) : 'none')

const ACCESS_WORDS: Record<string, string> = {
  members: 'group members only',
  plus: 'members and their friends',
  public: 'anyone',
}

const CALENDAR_FIELDS: Record<string, Say> = {
  title: { word: 'the title', show: quoted },
  description: 'the description',
  imageUrl: 'the picture',
  vrchatImageId: 'the picture',
  category: { word: 'the category', show: plain },
  startsAt: { say: (_, __, whole) => `the time, now ${moment(whole['startsAt'])}` },
  endsAt: { say: (_, __, whole) => `the time, now ${moment(whole['startsAt'])}` },
  timeZone: { word: 'the time zone', show: plain },
  repeat: { say: (_, __, whole) => `how it repeats, now ${repeatWords(whole)}` },
  repeatDays: { say: (_, __, whole) => `how it repeats, now ${repeatWords(whole)}` },
  repeatUntil: { word: 'the last date', show: plain },
  worldId: { say: (was, now) => (now ? (was ? 'the world' : 'a world added') : 'the world taken off') },
  accessType: { word: 'who can join', show: (v) => ACCESS_WORDS[String(v)] ?? plain(v) },
  region: { word: 'the region', show: (v) => plain(v).toUpperCase() },
  languages: { word: 'the languages', show: plain },
  platforms: { word: 'the platforms', show: plain },
  tags: { word: 'the tags', show: plain },
  visibility: { word: 'who can see it', show: plain },
  notifyMembers: { toggle: 'notifying members' },
  publishToVRChat: { toggle: "VRChat's calendar" },
  publishToDiscord: { toggle: 'the Discord event' },
  postToChannel: { toggle: 'the channel post' },
  channelId: 'the channel',
  autoOpen: { toggle: 'opening the instance' },
  openMinutesBefore: { word: 'how early the instance opens', show: (v) => `${plain(v)} minutes` },
}

const GIVEAWAY_FIELDS: Record<string, Say> = {
  name: { word: 'the name', show: quoted },
  prize: { word: 'the prize', show: plain },
  opensAt: { word: 'when it opens', show: moment },
  closesAt: { word: 'when it closes', show: moment },
  drawAt: { word: 'when it is drawn', show: (v) => (v ? moment(v) : 'by hand') },
  winnerCount: { word: 'the number of winners', show: plain },
  entryWay: { word: 'how to enter', show: (v) => (v === 'react' ? 'reacting' : plain(v)) },
  emoji: { word: 'the emoji', show: plain },
  rules: 'who can enter',
  exclusions: 'who is left out',
  weighting: { word: 'the odds', show: (v) => (v === 'uniform' ? 'equal' : fieldName(plain(v))) },
  weightCap: 'the odds',
  postToChannel: { toggle: 'the channel post' },
  channelId: 'the channel',
}

const WEBHOOK_FIELDS: Record<string, Say> = {
  name: { word: 'the name', show: quoted },
  url: { word: 'the address', show: plain },
  eventTypes: { word: 'the events it sends', show: plain },
  subjects: 'the people it follows',
}

/**
 * Permissions as Discord's own settings name them: "ManageGuild" is "Manage Server" there, and
 * "ModerateMembers" is "Timeout Members". Stored as Discord.Net's names, which no moderator sees.
 * A name this build does not know is spelled out from the stored one.
 */
const DISCORD_PERMISSIONS: Record<string, string> = {
  AddReactions: 'Add Reactions',
  Administrator: 'Administrator',
  AttachFiles: 'Attach Files',
  BanMembers: 'Ban Members',
  BypassSlowmode: 'Bypass Slowmode',
  ChangeNickname: 'Change Nickname',
  Connect: 'Connect',
  CreateEvents: 'Create Events',
  CreateGuildExpressions: 'Create Expressions',
  CreateInstantInvite: 'Create Invite',
  CreatePrivateThreads: 'Create Private Threads',
  CreatePublicThreads: 'Create Public Threads',
  DeafenMembers: 'Deafen Members',
  EmbedLinks: 'Embed Links',
  KickMembers: 'Kick Members',
  ManageChannels: 'Manage Channels',
  ManageEmojisAndStickers: 'Manage Expressions',
  ManageEvents: 'Manage Events',
  ManageGuild: 'Manage Server',
  ManageMessages: 'Manage Messages',
  ManageNicknames: 'Manage Nicknames',
  ManageRoles: 'Manage Roles',
  ManageThreads: 'Manage Threads and Posts',
  ManageWebhooks: 'Manage Webhooks',
  MentionEveryone: 'Mention @everyone, @here, and All Roles',
  ModerateMembers: 'Timeout Members',
  MoveMembers: 'Move Members',
  MuteMembers: 'Mute Members',
  PinMessages: 'Pin Messages',
  PrioritySpeaker: 'Priority Speaker',
  ReadMessageHistory: 'Read Message History',
  RequestToSpeak: 'Request to Speak',
  SendMessages: 'Send Messages and Create Posts',
  SendMessagesInThreads: 'Send Messages in Threads and Posts',
  SendPolls: 'Create Polls',
  SendTTSMessages: 'Send Text-to-Speech Messages',
  SendVoiceMessages: 'Send Voice Messages',
  SetVoiceChannelStatus: 'Set Voice Channel Status',
  Speak: 'Speak',
  StartEmbeddedActivities: 'Use Activities',
  Stream: 'Video',
  UseApplicationCommands: 'Use Application Commands',
  UseClydeAI: 'Use Clyde AI',
  UseExternalApps: 'Use External Apps',
  UseExternalEmojis: 'Use External Emoji',
  UseExternalSounds: 'Use External Sounds',
  UseExternalStickers: 'Use External Stickers',
  UseSoundboard: 'Use Soundboard',
  UseVAD: 'Use Voice Activity',
  ViewAuditLog: 'View Audit Log',
  ViewChannel: 'View Channels',
  ViewGuildInsights: 'View Server Insights',
  ViewMonetizationAnalytics: 'View Server Subscription Insights',
}

/** The stored permission names, as Discord's settings show them. */
function permissions(value: unknown): string[] {
  if (!Array.isArray(value)) return []

  return value
    .filter((v): v is string => typeof v === 'string')
    .map((v) => DISCORD_PERMISSIONS[v] ?? v.replace(/([a-z0-9])([A-Z])/g, '$1 $2'))
}

/** "Modbot could not add “Movie night” to VRChat's calendar", for each place an event goes. */
function publishFailure(place: string | null, action: string | null, title: React.ReactNode): React.ReactNode {
  if (place === 'discordEvent') return <>Modbot could not publish{title} as a Discord event</>
  if (place === 'channelPost') return <>Modbot could not post{title} in the Discord channel</>

  if (action === 'update') return <>Modbot could not update{title} on VRChat's calendar</>
  if (action === 'delete') return <>Modbot could not take{title} off VRChat's calendar</>
  return <>Modbot could not add{title} to VRChat's calendar</>
}

/** "Ada, Mira and Sam" out of a draw's winners, first place first. */
function winnerNames(value: unknown): string | null {
  if (!Array.isArray(value) || value.length === 0) return null

  const names = value
    .map((w) => record(w))
    .filter((w): w is Record<string, unknown> => w !== null)
    .sort((a, b) => (Number(a['rank']) || 0) - (Number(b['rank']) || 0))
    .map((w) => (typeof w['name'] === 'string' && w['name'] ? w['name'] : 'somebody'))

  return names.length > 0 ? list(names) : null
}

/** "The term list “Slurs”", or "the topic “Scams”" mid-sentence. */
function rule(kind: string | null, name: string | null, capital = true): React.ReactNode {
  const what = kind === 'termList' ? 'term list' : kind === 'topic' ? 'topic' : 'rule'

  if (!name) return `${capital ? 'An' : 'an'} AutoMod ${what}`

  return (
    <>
      {capital ? 'The' : 'the'} {what}
      <Quoted value={name} />
    </>
  )
}

/** Where AutoMod read something, said the way a person would. */
function targetWord(target: string | null): string {
  const words: Record<string, string> = {
    discordMessage: 'Discord message',
    displayName: 'display name',
    bio: 'bio',
    status: 'status',
    pronouns: 'pronouns',
  }

  if (!target) return 'words'
  return words[target] ?? fieldName(target)
}

/** The names of the rules that made AutoMod act, from an action fact. */
function actingRules(value: unknown): string[] {
  if (!Array.isArray(value)) return []

  return value
    .map((r) => record(r)?.['ruleName'])
    .filter((n): n is string => typeof n === 'string' && n.length > 0)
}

/**
 * Something AutoMod did, or tried to do. The fact is written either way, so `done` decides which
 * sentence it is.
 */
function AutoModAction({ p, did, tried }: { p: Parts; did: React.ReactNode; tried: React.ReactNode }) {
  const names = actingRules(p.entry.data?.['rules'])
  const because =
    names.length > 0 ? ` because of ${names.length === 1 ? 'the rule' : 'the rules'} ${list(names.map((n) => `“${n}”`))}` : null

  if (p.entry.data?.['done'] === false)
    return (
      <>
        AutoMod tried to {tried}
        {because} but could not{p.text('error') ? <>: {p.text('error')}</> : null}.
      </>
    )

  return (
    <>
      AutoMod {did}
      {because}.
    </>
  )
}

/** A change to an AutoMod rule or to AutoMod's settings, by what kind of change it was. */
function ruleChange(p: Parts): React.ReactNode {
  const change = p.text('change')

  if (p.text('ruleKind') === 'settings') {
    if (change === 'switched-on') return <>{p.actor} switched AutoMod on</>
    if (change === 'switched-off') return <>{p.actor} switched AutoMod off</>
    return <>{p.actor} changed AutoMod's settings</>
  }

  const it = rule(p.text('ruleKind'), p.text('ruleName'), false)

  switch (change) {
    case 'created':
      return <>{p.actor} created {it}</>
    case 'deleted':
      return <>{p.actor} deleted {it}</>
    case 'added-from-hub':
      return <>{p.actor} added {it} from the shared lists</>
    case 'updated-from-hub':
      return <>{p.actor} updated {it} from the shared lists</>
    case 'act-without-test':
      return <>{p.actor} let {it} act without testing it first</>
    case 'trial-ended':
      return <>{p.actor} ended the test of {it}, so it now acts</>
    case 'resumed':
      return <>{p.actor} started {it} again after it paused itself</>
    default:
      return <>{p.actor} changed {it}</>
  }
}

/** "12 added, 3 already known, 1 rejected" for an import, leaving out the zeros. */
function importCounts(data: Record<string, unknown> | null): string | null {
  const counts: [string, string][] = [
    ['imported', 'added'],
    ['alreadyKnown', 'already known'],
    ['skipped', 'skipped'],
    ['rejected', 'rejected'],
  ]

  const said = counts.flatMap(([key, word]) => {
    const n = data?.[key]
    return typeof n === 'number' && n > 0 ? [`${n.toLocaleString()} ${word}`] : []
  })

  return said.length > 0 ? said.join(', ') : null
}
