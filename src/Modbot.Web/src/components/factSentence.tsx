import { AccountLink, PersonLink, InstanceLink, WorldLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import type { AuditEntry } from '@/lib/api'
import { openPersonVersion } from '@/lib/subject'
import { avatarWorn, timeInInstance } from '@/lib/factDetails'

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
 * "the Discord role Moderator" when the name is there, "a Discord role" when it is not. Dropping a
 * placeholder into the sentence instead gives "the Discord role a role", which is not a sentence:
 * a name that is missing changes the shape of the clause around it, not just the word in the hole.
 */
function named(name: string | null | undefined, kind: string): React.ReactNode {
  return name ? <>the {kind} {name}</> : `a ${kind}`
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

function changedFields(entry: AuditEntry): [string, { old?: unknown; new?: unknown }][] {
  const changed = entry.data?.['changed']
  if (!changed || typeof changed !== 'object') return []

  return Object.entries(changed as Record<string, { old?: unknown; new?: unknown }>)
}

/** "name, from The Black Cat to The Red Cat" — the diff the producers all record the same way. */
function Changed({ changed }: { changed: Parts['changed'] }) {
  if (changed.length === 0) return null

  if (changed.length === 1) {
    const [key, pair] = changed[0]
    return (
      <>
        {' '}
        : {fieldName(key)}, from {shown(pair.old)} to {shown(pair.new)}
      </>
    )
  }

  return <>: {changed.map(([key]) => fieldName(key)).join(', ')} changed</>
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
      {p.actor} gave {p.subject} the role {p.text('roleName') ?? 'a role'}.
    </>
  ),

  'vrchat.group.role.unassign': (p) => (
    <>
      {p.actor} took the role {p.text('roleName') ?? 'a role'} away from {p.subject}.
    </>
  ),

  'vrchat.group.invite.create': (p) => <>{p.actor} invited {p.subject} to the group.</>,

  'vrchat.group.update': (p) => (
    <>
      {p.actor} changed the group's details<Changed changed={p.changed} />.
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
      {p.actor} changed the role {p.text('roleName') ?? 'a role'}
      <Changed changed={p.changed} />.
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
  'discord.voice.join': (p) => <>{p.subject} joined a Discord voice channel.</>,
  'discord.voice.leave': (p) => <>{p.subject} left a Discord voice channel.</>,
  'discord.voice.move': (p) => <>{p.subject} moved to another Discord voice channel.</>,

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
  'discord.channel.create': (p) => <>{p.actor} created the Discord channel {p.text('name') ?? 'a channel'}.</>,
  'discord.channel.update': (p) => <>{p.actor} changed the Discord channel {p.text('name') ?? 'a channel'}.</>,
  'discord.channel.delete': (p) => <>{p.actor} deleted the Discord channel {p.text('name') ?? 'a channel'}.</>,
  'discord.role.create': (p) => <>{p.actor} created {named(p.text('name'), 'Discord role')}.</>,
  'discord.role.update': (p) => <>{p.actor} changed {named(p.text('name'), 'Discord role')}.</>,
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

  'modbot.role.create': (p) => <>{p.actor} created the Modbot role {p.text('name') ?? ''}.</>,
  'modbot.role.change': (p) => (
    <>
      {p.actor} changed the Modbot role {p.text('name') ?? ''}
      <Changed changed={p.changed} />.
    </>
  ),
  'modbot.role.delete': (p) => <>{p.actor} deleted the Modbot role {p.text('name') ?? ''}.</>,

  'modbot.apikey.create': (p) => <>{p.actor} created an API key.</>,
  'modbot.apikey.revoke': (p) => <>{p.actor} revoked an API key.</>,

  'modbot.settings.change': (p) => (
    <>
      {p.actor} changed {p.text('setting') ? `the ${fieldName(p.text('setting')!)} setting` : "Modbot's settings"}
      {p.text('before') !== null || p.text('after') !== null ? (
        <>, from {p.text('before') || 'nothing'} to {p.text('after') || 'nothing'}</>
      ) : null}
      <Changed changed={p.changed} />.
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

      <details className="mt-1">
        <summary className="cursor-pointer text-muted-foreground">
          VRChat called it{' '}
          <span className="font-mono">{eventType ?? 'nothing at all'}</span>. Show what it sent
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
