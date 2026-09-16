import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { ago, formatDay } from '@/lib/format'
import { api, ApiError, type CurrentUser, type LinkedDiscord, type LinkedFilter, type MemberList, type MemberQuery } from '@/lib/api'
import { can, canAny } from '@/lib/permissions'
import { useQueryParam } from '@/lib/router'
import { cn } from '@/lib/utils'

/**
 * The group's member list, as the member sweep last read it.
 *
 * Everything here is real and says how old it is. The list is the swept table; the names and
 * pictures are whatever the profile sync has fetched so far, and a row it has not reached yet
 * shows the id rather than a placeholder name. "Last synced" is the end of the last full sweep,
 * never an implied guarantee (spec 4.2.3), and before the first sweep has finished the page says
 * plainly that what it shows is partial.
 *
 * Search, the role filter and paging all run on the server: a five-thousand-row list is not
 * something to hand a browser to filter.
 */

const PAGE_SIZE = 50

export function Members({ me, onOpenSubject }: { me: CurrentUser; onOpenSubject: (id: string) => void }) {
  // Links are for people who may see profiles; the server leaves them out for anybody else.
  const seesLinks = can(me, 'ViewProfile')

  // Whether this moderator has anything to offer on a row at all, so an empty column is not drawn
  // for everybody who cannot act.
  const canAct = canAny(me, ['Kick', 'Ban'])

  // The stretch an unusual-activity alert links to. In the address bar rather than in state, so
  // the link a moderator was sent lands on the same list they were meant to see.
  const [joinedFrom, setJoinedFrom] = useQueryParam('joinedFrom')
  const [joinedTo, setJoinedTo] = useQueryParam('joinedTo')
  const joined = joinedFrom && joinedTo ? { from: joinedFrom, to: joinedTo } : null

  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [role, setRole] = useState('')
  const [status, setStatus] = useState<NonNullable<MemberQuery['status']>>('current')
  const [sort, setSort] = useState<NonNullable<MemberQuery['sort']>>('joined')
  const [linked, setLinked] = useState<LinkedFilter>('all')
  const [page, setPage] = useState(1)
  const [list, setList] = useState<MemberList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Bumped after a kick or a ban. The server has already marked the person as gone, so this
  // re-reads the list rather than editing the row in place and hoping the two agree.
  const [acted, setActed] = useState(0)

  // Typing waits a moment before it asks, so a name typed at speed is one request, not nine.
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(typed.trim())
      setPage(1)
    }, 300)
    return () => clearTimeout(timer)
  }, [typed])

  useEffect(() => {
    let cancelled = false

    api
      .members({
        search,
        role,
        status,
        sort,
        linked,
        joinedFrom: joined?.from,
        joinedTo: joined?.to,
        page,
        pageSize: PAGE_SIZE,
      })
      .then((next) => {
        if (cancelled) return
        setList(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view members.'
            : 'Could not load the member list.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [search, role, status, sort, linked, joined?.from, joined?.to, page, acted])

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))

  return (
    <div className="flex flex-col gap-3">
      <Freshness coverage={list.coverage} />

      <Card>
        <CardContent className="p-0">
          <div
            className="flex flex-wrap items-center gap-2 border-b px-3 py-2"
            style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          >
            <Input
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              placeholder="Search by name or id"
              className="h-8 w-64"
              aria-label="Search members"
            />

            <Select
              value={role}
              onChange={(v) => {
                setRole(v)
                setPage(1)
              }}
              aria-label="Role"
            >
              <option value="">Any role</option>
              {list.roles.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name ?? r.id}
                </option>
              ))}
            </Select>

            <Select
              value={status}
              onChange={(v) => {
                setStatus(v as typeof status)
                setPage(1)
              }}
              aria-label="Status"
            >
              <option value="current">Members</option>
              <option value="left">People who left</option>
              <option value="all">Both</option>
            </Select>

            <Select
              value={sort}
              onChange={(v) => {
                setSort(v as typeof sort)
                setPage(1)
              }}
              aria-label="Sort"
            >
              <option value="joined">Newest joiner first</option>
              <option value="name">By name</option>
              <option value="seen">Most recently seen first</option>
            </Select>

            {joined && (
              <Button
                size="sm"
                variant="outline"
                className="h-8"
                onClick={() => {
                  setJoinedFrom(null)
                  setJoinedTo(null)
                  setPage(1)
                }}
              >
                {`Joined ${new Date(joined.from).toLocaleString()} – ${new Date(joined.to).toLocaleTimeString()} ×`}
              </Button>
            )}

            {seesLinks && (
              <Select
                value={linked}
                onChange={(v) => {
                  setLinked(v as LinkedFilter)
                  setPage(1)
                }}
                aria-label="Linked"
              >
                <option value="all">All</option>
                <option value="linked">Linked</option>
                <option value="not-linked">Not linked</option>
              </Select>
            )}

            <span className="flex-1" />

            <span className="text-muted-foreground">
              {list.total.toLocaleString()} {list.total === 1 ? 'person' : 'people'}
            </span>
          </div>

          {list.members.length === 0 ? (
            <div className="py-10 text-center text-muted-foreground">
              <div className="font-medium text-foreground">
                {search || role || linked !== 'all' ? 'Nobody matches' : 'Nobody listed yet'}
              </div>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">Person</th>
                    {seesLinks && <th className="px-3 py-2 text-left font-normal">Discord</th>}
                    <th className="px-3 py-2 text-left font-normal">Roles</th>
                    <th className="px-3 py-2 text-left font-normal">Joined</th>
                    <th className="px-3 py-2 text-left font-normal">Last seen by Modbot</th>
                    {status !== 'current' && <th className="px-3 py-2 text-left font-normal">Left</th>}
                    {canAct && <th className="px-3 py-2 text-left font-normal"><span className="sr-only">Actions</span></th>}
                  </tr>
                </thead>
                <tbody>
                  {list.members.map((m) => (
                    <tr
                      key={m.userId}
                      className={cn(
                        'border-b last:border-0 hover:bg-muted/40',
                        m.leftAt && 'text-muted-foreground',
                      )}
                      style={{ borderBottomWidth: 'var(--hairline)' }}
                    >
                      <td className="px-3" style={{ height: 'var(--row-h)' }}>
                        <div className="flex items-center gap-2">
                          {m.avatarThumbnailUrl ? (
                            <img
                              src={m.avatarThumbnailUrl}
                              alt=""
                              className="size-7 shrink-0 rounded-full bg-muted object-cover"
                              referrerPolicy="no-referrer"
                            />
                          ) : (
                            <div className="size-7 shrink-0 rounded-full bg-muted" />
                          )}
                          <div className="min-w-0">
                            <div className="flex items-center gap-1.5">
                              <SubjectLink id={m.userId} name={m.displayName} onOpen={onOpenSubject} />
                              {m.eighteenPlus && (
                                <span
                                  className="inline-flex items-center rounded-full border border-transparent bg-ok/15 px-1.5 py-0 font-medium text-ok"
                                  style={{ fontSize: '0.6875rem' }}
                                  title="18+ verified"
                                >
                                  18+
                                </span>
                              )}
                              {m.isRepresenting && (
                                <span
                                  className="text-muted-foreground"
                                  style={{ fontSize: '0.6875rem' }}
                                >
                                  representing
                                </span>
                              )}
                            </div>
                            {m.displayName && (
                              <div className="truncate font-mono text-muted-foreground/70" style={{ fontSize: '0.6875rem' }}>
                                {m.userId}
                              </div>
                            )}
                          </div>
                        </div>
                      </td>
                      {seesLinks && (
                        <td className="px-3">
                          {m.linkedDiscord ? (
                            <DiscordAccount account={m.linkedDiscord} />
                          ) : (
                            <span className="text-muted-foreground">—</span>
                          )}
                        </td>
                      )}
                      <td className="px-3">
                        <div className="flex flex-wrap gap-1">
                          {m.roleNames.map((name, i) => (
                            <Badge key={m.roleIds[i] ?? name} variant="secondary" title={m.roleIds[i]}>
                              {name}
                            </Badge>
                          ))}
                          {m.roleNames.length === 0 && <span className="text-muted-foreground">—</span>}
                        </div>
                      </td>
                      <td className="px-3 tabular-nums">
                        {m.joinedAt ? formatDay(m.joinedAt) : <span className="text-muted-foreground">—</span>}
                      </td>
                      <td className="px-3 text-muted-foreground">
                        {m.lastSeenAt ? ago(m.lastSeenAt, list.coverage.now) : '—'}
                      </td>
                      {status !== 'current' && (
                        <td className="px-3 tabular-nums">{m.leftAt ? formatDay(m.leftAt) : ''}</td>
                      )}
                      {canAct && (
                        <td className="px-3 text-right">
                          <ModerationActions
                            me={me}
                            person={{ userId: m.userId, isMember: !m.leftAt }}
                            name={m.displayName ?? m.userId}
                            onDone={() => setActed((n) => n + 1)}
                            size="xs"
                            layout="menu"
                          />
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {pages > 1 && (
            <div
              className="flex items-center gap-2 border-t px-3 py-2"
              style={{ borderTopWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
            >
              <Button variant="outline" size="xs" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                Previous
              </Button>
              <span className="text-muted-foreground">
                Page {list.page} of {pages}
              </span>
              <Button variant="outline" size="xs" disabled={page >= pages} onClick={() => setPage(page + 1)}>
                Next
              </Button>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

/** A group member's linked Discord account: picture, name, and whether they are in the server. */
function DiscordAccount({ account }: { account: LinkedDiscord }) {
  return (
    <div className="flex items-center gap-2">
      <Avatar url={account.avatarUrl} className="size-6" />
      <div className="min-w-0">
        <DiscordPersonLink id={account.userId} name={account.name} />
        <div className="text-muted-foreground" style={{ fontSize: '0.6875rem' }}>
          {account.inServer ? 'In server' : account.leftAt ? 'Left' : 'Not in server'}
        </div>
      </div>
    </div>
  )
}

/**
 * How old the list is, stated before it. Before the first full sweep the list is partial and
 * the notice is the warning colour, because a short list shown as the group is the mistake
 * this page most needs to not make.
 */
function Freshness({ coverage }: { coverage: MemberList['coverage'] }) {
  if (!coverage.firstSweepComplete) {
    return (
      <div
        className="rounded-xl border border-warn/40 bg-warn/10 px-4 py-3"
        style={{ borderWidth: 'var(--hairline)' }}
      >
        <div className="font-medium">
          {coverage.sweepInProgress
            ? 'Reading the member list for the first time.'
            : 'The member list has not been read yet.'}
        </div>
      </div>
    )
  }

  return (
    <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
      <span>
        Last synced {ago(coverage.lastSyncedAt, coverage.now)}
        {coverage.sweepInProgress ? '. A new sweep is running now' : ''}.
      </span>
      <span>
        {coverage.memberCount.toLocaleString()} {coverage.memberCount === 1 ? 'member' : 'members'} at the last full
        sweep.
      </span>
    </div>
  )
}

export function Select({
  value,
  onChange,
  children,
  ...rest
}: {
  value: string
  onChange: (value: string) => void
  children: React.ReactNode
  'aria-label': string
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="h-8 rounded-md border border-input bg-transparent px-2 text-foreground"
      style={{ fontSize: 'var(--text-small)' }}
      {...rest}
    >
      {children}
    </select>
  )
}

export function Empty({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
    </Card>
  )
}
