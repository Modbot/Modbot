import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Tabs } from '@/components/ui/tabs'
import { DailyBars, compactNumber, dateTime, minutes } from '@/components/charts'
import { Avatar, RoleChip } from '@/components/discord/DiscordMemberParts'
import { SubjectLink } from '@/components/facts'
import { JsonView } from '@/components/JsonView'
import { FactList, Field, Figure, Note, Panel, PopupFrame } from '@/components/subject/shared'
import { api, ApiError, type AuditEntry, type CurrentUser, type DiscordMember } from '@/lib/api'
import { formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'
import { useMessageAt, useOpeningTab } from '@/lib/subject'
import { cn } from '@/lib/utils'
import { useLoad } from '@/lib/useLoad'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'

const TABS = ['overview', 'logs', 'history', 'messages', 'metrics', 'json'] as const
type Tab = (typeof TABS)[number]

/** The facts that are this account's own history in the server: coming, going, and what was done to them. */
const HISTORY_TYPES = [
  'discord.member.join',
  'discord.member.leave',
  'discord.member.nickname',
  'discord.role.assign',
  'discord.role.unassign',
  'discord.member.ban',
  'discord.member.unban',
  'discord.member.kick',
  'discord.member.timeout',
  'discord.member.timeout.remove',
  'discord.link.create',
  'discord.link.remove',
]

/**
 * One Discord account: who they are in the server on the left, and what Modbot has recorded about
 * them on the right.
 *
 * A separate popup from the VRChat person's, even for somebody who linked. The two accounts have
 * separate histories and most people are only on one side, so each popup shows its own side and
 * points at the other when there is a link.
 *
 * There is no Cases tab. Case files are written against a VRChat ban and snapshot a VRChat
 * profile, so a Discord account cannot have one yet.
 */
export function DiscordPersonPopup({ id, me, lead }: { id: string; me: CurrentUser; lead?: React.ReactNode }) {
  // Opened at one message, from a source chip under a Chat answer: the Messages tab, on the page
  // that holds it, with that message marked.
  const at = useMessageAt()
  const reads = can(me, 'ReadDiscordMessages')

  const [tab, setTab] = useOpeningTab<Tab>(at && reads ? 'messages' : 'overview', TABS)

  const tabs: { value: Tab; label: string }[] = [
    { value: 'overview', label: 'Overview' },
    { value: 'logs', label: 'Logs' },
    { value: 'history', label: 'History' },
    ...(reads ? [{ value: 'messages' as const, label: 'Messages' }] : []),
    ...(can(me, 'ViewProfile') ? [{ value: 'metrics' as const, label: 'Metrics' }] : []),
    { value: 'json', label: 'JSON' },
  ]

  return (
    <PopupFrame
      title="Discord person"
      subtitle={<span className="font-mono" title={id}>{id}</span>}
      lead={lead}
      left={
        <>
          {can(me, 'ViewMembers') && <Identity id={id} />}
          {can(me, 'ViewProfile') && <LinkedVRChatCard id={id} me={me} />}
        </>
      }
    >
      <Tabs value={tab} onChange={setTab} tabs={tabs}>
        {tab === 'overview' && <Overview id={id} me={me} onMore={setTab} />}
        {tab === 'logs' && <Logs id={id} />}
        {tab === 'history' && <History id={id} />}
        {tab === 'messages' && <Messages id={id} at={at} />}
        {tab === 'metrics' && <Metrics id={id} />}
        {tab === 'json' && <Records id={id} me={me} />}
      </Tabs>
    </PopupFrame>
  )
}

/** The glance: the activity figures and the newest facts about this account. */
function Overview({ id, me, onMore }: { id: string; me: CurrentUser; onMore: (tab: Tab) => void }) {
  const seesProfile = can(me, 'ViewProfile')

  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id, 'Discord'), [id]))

  const loadMetrics = useCallback(() => api.discordMemberMetrics(id), [id])
  const metrics = useLoad(seesProfile ? loadMetrics : null, live)

  const loadFacts = useCallback(() => api.audit({ subject: id, subjectPlatform: 'Discord', limit: 8 }), [id])
  const facts = useLoad(loadFacts, live)

  const sum = (points: { value: number }[]) => points.reduce((total, p) => total + p.value, 0)

  return (
    <div className="flex flex-col gap-3 p-4">
      {metrics.data && (
        <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
          <Figure label="Messages, 30 days" value={compactNumber(sum(metrics.data.messagesPerDay))} />
          <Figure label="Voice, 30 days" value={minutes(sum(metrics.data.voiceMinutesPerDay))} />
          <Figure label="Messages, all time" value={compactNumber(metrics.data.messagesAllTime)} />
          <Figure label="First seen" value={metrics.data.firstSeenAt ? formatDay(metrics.data.firstSeenAt) : '—'} />
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

/** Coming, going, renames, roles, timeouts and links: this account's own history in the server, newest first. */
function History({ id }: { id: string }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id, 'Discord'), [id]))
  const load = useCallback(
    () => api.audit({ subject: id, subjectPlatform: 'Discord', type: HISTORY_TYPES, limit: 100 }),
    [id],
  )
  const { data, error } = useLoad(load, live)

  return (
    <Panel title="In the server">
      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </Panel>
  )
}

/** The stored records, verbatim: the member row as the API answers it, and the activity counts. */
function Records({ id, me }: { id: string; me: CurrentUser }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id, 'Discord'), [id]))

  const loadMember = useCallback(
    () => api.discordMember(id).catch((e: unknown) => (e instanceof ApiError && e.status === 404 ? null : Promise.reject(e))),
    [id],
  )
  const member = useLoad(can(me, 'ViewMembers') ? loadMember : null, live)

  const loadMetrics = useCallback(() => api.discordMemberMetrics(id), [id])
  const metrics = useLoad(can(me, 'ViewProfile') ? loadMetrics : null, live)

  return (
    <div className="flex flex-col gap-3 p-4">
      {can(me, 'ViewMembers') && <JsonView title="Member" value={member.error ?? member.data} />}
      {can(me, 'ViewProfile') && <JsonView title="Activity" value={metrics.error ?? metrics.data} />}
      {!can(me, 'ViewMembers') && !can(me, 'ViewProfile') && <Note>You do not have permission to see this.</Note>}
    </div>
  )
}

/** The person as the stored member list has them. Somebody the bot never saw in the server says so. */
function Identity({ id }: { id: string }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id, 'Discord'), [id]))

  // A 404 is an answer, not a failure: somebody named in a fact who never was in the server.
  const load = useCallback(
    () =>
      api
        .discordMember(id)
        // Whether the timeout is still running is worked out when the answer arrives, not on every render.
        .then((member): { member: DiscordMember | null; timedOut: boolean } => ({
          member,
          timedOut: member.timedOutUntil !== null && Date.parse(member.timedOutUntil) > Date.now(),
        }))
        .catch((e: unknown) => {
          if (e instanceof ApiError && e.status === 404) return { member: null, timedOut: false }
          throw e
        }),
    [id],
  )
  const { data, error } = useLoad(load, live)

  if (error) return <Note className="text-destructive">{error}</Note>
  if (!data) return <Note>Loading…</Note>

  const member = data.member
  if (!member) return <Note>Not seen in the server.</Note>

  const timedOut = data.timedOut

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-3">
        <Avatar url={member.avatarUrl} className="size-14" />
        <div className="min-w-0">
          <div className="truncate font-medium">{member.displayName}</div>
          {member.globalName && member.globalName !== member.displayName && (
            <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {member.globalName}
            </div>
          )}
          <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            @{member.username}
          </div>
        </div>
      </div>

      <div className="flex flex-wrap gap-1">
        {member.leftAt ? <Badge variant="outline">Left</Badge> : <Badge variant="secondary">In server</Badge>}
        {member.isBot && <Badge variant="outline">Bot</Badge>}
        {member.isPending && <Badge variant="outline">Pending</Badge>}
        {timedOut && <Badge variant="destructive">Timed out</Badge>}
      </div>

      {member.joinedAt && <Field label="Joined">{formatDay(member.joinedAt)}</Field>}
      {member.leftAt && <Field label="Left">{formatDay(member.leftAt)}</Field>}
      {timedOut && member.timedOutUntil && <Field label="Timed out until">{dateTime(member.timedOutUntil)}</Field>}
      {member.boostingSince && <Field label="Boosting since">{formatDay(member.boostingSince)}</Field>}

      {member.roles.length > 0 && (
        <Field label="Roles">
          <span className="mt-0.5 flex flex-wrap gap-1">
            {member.roles.map((r) => (
              <RoleChip key={r.id} id={r.id} name={r.name} color={r.color} />
            ))}
          </span>
        </Field>
      )}
    </div>
  )
}

/** The VRChat account this Discord account is linked to, opening the VRChat person popup. */
function LinkedVRChatCard({ id, me }: { id: string; me: CurrentUser }) {
  // Keyed on a count, so an unlink reads the link again from scratch.
  const [version, setVersion] = useState(0)
  return <LinkedVRChat key={version} id={id} me={me} onUnlinked={() => setVersion((v) => v + 1)} />
}

function LinkedVRChat({ id, me, onUnlinked }: { id: string; me: CurrentUser; onUnlinked: () => void }) {
  const [busy, setBusy] = useState(false)
  const [unlinkError, setUnlinkError] = useState<string | null>(null)

  const load = useCallback(() => api.discordLinkForDiscord(id), [id])
  const { data, error } = useLoad(load)
  const link = data?.link ?? null

  if (error) return <Note className="text-destructive">{error}</Note>
  if (!link) return null

  const unlink = () => {
    setBusy(true)
    api
      .unlinkDiscord(link.id)
      .then(onUnlinked)
      .catch((e: unknown) => setUnlinkError(e instanceof ApiError ? e.message : 'Could not unlink.'))
      .finally(() => setBusy(false))
  }

  return (
    <div
      className="rounded-md border px-3 py-2"
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      <div className="font-medium">VRChat</div>
      <div className="mt-1 flex flex-col gap-1.5">
        <SubjectLink id={link.vrChatUserId} name={link.vrChatDisplayName} />
        <p className="text-muted-foreground">Linked {formatDay(link.linkedAt)}</p>
        {unlinkError && <p className="text-destructive">{unlinkError}</p>}
        {can(me, 'ManageDiscordLinks') && (
          <div>
            <Button type="button" variant="outline" size="sm" onClick={unlink} disabled={busy}>
              Unlink
            </Button>
          </div>
        )}
      </div>
    </div>
  )
}

const LOG_LIMIT = 50

/**
 * Facts about this account and facts it did, newest first.
 *
 * Two reads merged rather than one, because the log filters subject and actor together. The newest
 * fifty of each, merged and cut to fifty, are exactly the newest fifty of either.
 */
function Logs({ id }: { id: string }) {
  const load = useCallback(async () => {
    const [about, by] = await Promise.all([
      api.audit({ subject: id, subjectPlatform: 'Discord', limit: LOG_LIMIT }),
      api.audit({ actor: id, actorPlatform: 'Discord', limit: LOG_LIMIT }),
    ])

    const seen = new Set<number>()
    const merged: AuditEntry[] = []
    for (const entry of [...about.entries, ...by.entries]) {
      if (seen.has(entry.id)) continue
      seen.add(entry.id)
      merged.push(entry)
    }

    merged.sort((a, b) => Date.parse(b.occurredAt) - Date.parse(a.occurredAt) || b.id - a.id)
    return merged.slice(0, LOG_LIMIT)
  }, [id])

  const { data, error } = useLoad(load)

  return (
    <div className="flex flex-col gap-3 p-4">
      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data} empty="Nothing recorded yet." />}
    </div>
  )
}

const MESSAGE_PAGE = 50

function Messages({ id, at }: { id: string; at?: string | null }) {
  const [page, setPage] = useState(1)

  // The server works out which page holds the message asked for, so a link lands on it rather than
  // on the newest page. Paging by hand afterwards drops the anchor.
  const [anchored, setAnchored] = useState(Boolean(at))
  const load = useCallback(
    () => api.discordMemberMessages(id, page, MESSAGE_PAGE, anchored ? at ?? undefined : undefined),
    [id, page, at, anchored],
  )
  const { data, error } = useLoad(load)

  if (error) return <Panel title="Messages"><Note className="text-destructive">{error}</Note></Panel>
  if (!data) return <Panel title="Messages"><Note>Loading…</Note></Panel>

  const pages = Math.max(1, Math.ceil(data.total / data.pageSize))

  // Turning a page by hand leaves the linked message behind, so the anchor goes with it. The page
  // turned from is the one the server answered with, which is where the linked message was found.
  const turn = (to: number) => {
    setAnchored(false)
    setPage(to)
  }

  return (
    <div className="flex min-h-0 flex-col gap-3 overflow-auto p-4">
      {data.messages.length === 0 ? (
        <Note>No messages stored.</Note>
      ) : (
        <ol className="flex flex-col gap-2">
          {data.messages.map((m) => (
            <Message key={m.messageId} marked={anchored && m.messageId === at}>
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium" title={m.channelId}>
                  #{m.channelName ?? m.channelId}
                </span>
                {m.threadId && (
                  <span className="text-muted-foreground" title={m.threadId}>
                    › {m.threadName ?? m.threadId}
                  </span>
                )}
                {m.editedAt && (
                  <Badge variant="outline" title={dateTime(m.editedAt)}>
                    Edited
                  </Badge>
                )}
                {m.deletedAt && (
                  <Badge variant="destructive" title={dateTime(m.deletedAt)}>
                    Deleted
                  </Badge>
                )}
                <span className="flex-1" />
                <span className="tabular-nums text-muted-foreground" title={new Date(m.sentAt).toLocaleString()}>
                  {dateTime(m.sentAt)}
                </span>
              </div>

              {m.text ? (
                <p className="mt-1 whitespace-pre-wrap break-words">{m.text}</p>
              ) : m.attachments.length === 0 ? (
                <p className="mt-1 text-muted-foreground">No text</p>
              ) : null}

              {m.attachments.length > 0 && (
                <ul className="mt-1 flex flex-wrap gap-2 text-muted-foreground">
                  {m.attachments.map((a, i) => (
                    <li key={`${a.name}:${i}`}>
                      {a.url ? (
                        <a href={a.url} target="_blank" rel="noreferrer noopener" className="underline">
                          {a.name}
                        </a>
                      ) : (
                        a.name
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </Message>
          ))}
        </ol>
      )}

      {pages > 1 && (
        <div className="flex items-center gap-2" style={{ fontSize: 'var(--text-small)' }}>
          <Button variant="outline" size="xs" disabled={data.page <= 1} onClick={() => turn(data.page - 1)}>
            Previous
          </Button>
          <span className="text-muted-foreground">
            Page {data.page} of {pages}
          </span>
          <Button variant="outline" size="xs" disabled={data.page >= pages} onClick={() => turn(data.page + 1)}>
            Next
          </Button>
        </div>
      )}
    </div>
  )
}

/** One message. A message somebody was sent a link to is marked and brought into view. */
function Message({ marked, children }: { marked: boolean; children: React.ReactNode }) {
  const row = useRef<HTMLLIElement>(null)
  const brought = useRef(false)

  useEffect(() => {
    if (!marked || brought.current) return
    brought.current = true
    row.current?.scrollIntoView({ block: 'center' })
  }, [marked])

  return (
    <li
      ref={row}
      className={cn('rounded-md border px-3 py-2', marked && 'border-ring bg-accent')}
      style={{ borderWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
    >
      {children}
    </li>
  )
}

function Metrics({ id }: { id: string }) {
  const load = useCallback(() => api.discordMemberMetrics(id), [id])
  const { data, error } = useLoad(load)

  if (error) return <Panel title="Metrics"><Note className="text-destructive">{error}</Note></Panel>
  if (!data) return <Panel title="Metrics"><Note>Loading…</Note></Panel>

  const sum = (points: { value: number }[]) => points.reduce((total, p) => total + p.value, 0)
  const from = data.messagesPerDay[0]?.day ?? ''
  const to = data.messagesPerDay[data.messagesPerDay.length - 1]?.day ?? ''

  return (
    <div className="flex min-h-0 flex-col overflow-auto">
      <Panel title="Activity">
        <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
          <Figure label="Messages, 30 days" value={compactNumber(sum(data.messagesPerDay))} />
          <Figure label="Voice, 30 days" value={minutes(sum(data.voiceMinutesPerDay))} />
          <Figure label="Messages, all time" value={compactNumber(data.messagesAllTime)} />
          <Figure label="Voice, all time" value={minutes(data.voiceMinutesAllTime)} />
          <Figure label="First seen" value={data.firstSeenAt ? formatDay(data.firstSeenAt) : '—'} />
          <Figure label="Joined" value={data.joinedAt ? formatDay(data.joinedAt) : '—'} />
        </div>
      </Panel>

      <Panel title="Messages per day">
        <DailyBars
          from={from}
          to={to}
          series={[{ key: 'messages', label: 'Messages', points: data.messagesPerDay, slot: 1 }]}
        />
      </Panel>

      <Panel title="Voice minutes per day">
        <DailyBars
          from={from}
          to={to}
          series={[{ key: 'voice', label: 'Minutes', points: data.voiceMinutesPerDay, slot: 2 }]}
        />
      </Panel>

      <Panel title="Joined and left">
        {data.history.length === 0 ? (
          <Note>Nothing recorded.</Note>
        ) : (
          <ol className="flex flex-col gap-1" style={{ fontSize: 'var(--text-small)' }}>
            {data.history.map((h, i) => (
              <li key={`${h.at}:${i}`} className="flex gap-2">
                <span className="w-14 font-medium">{h.change === 'joined' ? 'Joined' : 'Left'}</span>
                <span className="tabular-nums text-muted-foreground">
                  {h.before ? `${dateTime(h.at)} – ${dateTime(h.before)}` : dateTime(h.at)}
                </span>
              </li>
            ))}
          </ol>
        )}
      </Panel>
    </div>
  )
}
