import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DailyBars, compactNumber, dateTime, minutes } from '@/components/charts'
import { Avatar, RoleChip } from '@/components/discord/DiscordMemberParts'
import { FactList, Field, Figure, Note, Panel } from '@/components/subject/shared'
import { api, type DiscordMember } from '@/lib/api'
import type { DiscordMemberRead } from '@/lib/useDiscordMember'
import { formatDay } from '@/lib/format'
import { cn } from '@/lib/utils'
import { useLoad } from '@/lib/useLoad'
import { concernsPerson } from '@/lib/liveRules'
import type { LiveEvent } from '@/lib/liveStream'
import { useLiveVersion } from '@/lib/useLiveVersion'

/**
 * The Discord half of a person: who they are in the server, what happened to them there, and
 * what they wrote.
 *
 * These used to be a popup of their own, opened instead of the VRChat one. They are parts now
 * because a Discord account and a VRChat account are the same human being once they link, and a
 * moderator asking what happened to somebody should not have to know which of their accounts to
 * click (one view per person design §4).
 */

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

/** Who they are in the server: picture, names and the marks. Somebody the bot never saw says so. */
export function DiscordIdentity({ read }: { read: DiscordMemberRead }) {
  const { data, error } = read

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
    </div>
  )
}

/** The dates and roles, at the top of the Discord tab. */
function DiscordDetails({ member, timedOut }: { member: DiscordMember; timedOut: boolean }) {
  return (
    <div className="flex flex-col gap-2">
      <div className="font-medium">Details</div>

      <div className="flex flex-wrap gap-x-6 gap-y-2">
        {member.joinedAt && <Field label="Joined">{formatDay(member.joinedAt)}</Field>}
        {member.leftAt && <Field label="Left">{formatDay(member.leftAt)}</Field>}
        {timedOut && member.timedOutUntil && <Field label="Timed out until">{dateTime(member.timedOutUntil)}</Field>}
        {member.boostingSince && <Field label="Boosting since">{formatDay(member.boostingSince)}</Field>}
      </div>

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

/** Coming, going, renames, roles, timeouts and links: this account's own history in the server. */
export function DiscordHistory({ id, read }: { id: string; read: DiscordMemberRead }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id, 'Discord'), [id]))
  const load = useCallback(
    () => api.audit({ subject: id, subjectPlatform: 'Discord', type: HISTORY_TYPES, limit: 100 }),
    [id],
  )
  const { data, error } = useLoad(load, live)

  return (
    <div className="flex min-h-0 flex-col gap-3 overflow-auto p-4">
      {read.data?.member && <DiscordDetails member={read.data.member} timedOut={read.data.timedOut} />}

      <div className="font-medium">In the server</div>
      {error && <Note className="text-destructive">{error}</Note>}
      {!error && !data && <Note>Loading…</Note>}
      {data && <FactList entries={data.entries} empty="Nothing recorded yet." />}
    </div>
  )
}

const MESSAGE_PAGE = 50

export function DiscordMessages({ id, at }: { id: string; at?: string | null }) {
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

const sum = (points: { value: number }[]) => points.reduce((total, p) => total + p.value, 0)

export function DiscordMetrics({ id }: { id: string }) {
  const live = useLiveVersion(useCallback((event: LiveEvent) => concernsPerson(event, id, 'Discord'), [id]))
  const load = useCallback(() => api.discordMemberMetrics(id), [id])
  const { data, error } = useLoad(load, live)

  if (error) return <Panel title="Discord"><Note className="text-destructive">{error}</Note></Panel>
  if (!data) return <Panel title="Discord"><Note>Loading…</Note></Panel>

  const from = data.messagesPerDay[0]?.day ?? ''
  const to = data.messagesPerDay[data.messagesPerDay.length - 1]?.day ?? ''

  return (
    <>
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
    </>
  )
}
