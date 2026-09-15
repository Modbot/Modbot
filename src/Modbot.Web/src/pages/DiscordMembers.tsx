import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { dateTime } from '@/components/charts'
import { Avatar, RoleChip } from '@/components/discord/DiscordMemberParts'
import { SubjectLink } from '@/components/facts'
import { api, ApiError, type CurrentUser, type DiscordMemberList, type DiscordMemberQuery, type LinkedFilter } from '@/lib/api'
import { ago, formatDay } from '@/lib/format'
import { can } from '@/lib/permissions'
import { openDiscordPerson } from '@/lib/subject'
import { cn } from '@/lib/utils'
import { Empty, Select } from '@/pages/Members'

/**
 * The Discord server's members, as the bot keeps them.
 *
 * Separate from Members, and second to it. Most people are in the VRChat group or the Discord
 * server but not both, and most never link, so neither list can be a column on the other. A row
 * opens the Discord person popup; the linked VRChat name in it opens the VRChat one.
 *
 * Search, filters and paging all run on the server, like the group's list.
 */

const PAGE_SIZE = 50

export function DiscordMembers({ me }: { me: CurrentUser }) {
  const seesLinks = can(me, 'ViewProfile')

  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [state, setState] = useState<NonNullable<DiscordMemberQuery['state']>>('in-server')
  const [role, setRole] = useState('')
  const [linked, setLinked] = useState<LinkedFilter>('all')
  const [page, setPage] = useState(1)
  const [list, setList] = useState<DiscordMemberList | null>(null)
  const [error, setError] = useState<string | null>(null)

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
      .discordMembers({ search, state, role, linked, page, pageSize: PAGE_SIZE })
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
            : 'Could not load the Discord member list.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [search, state, role, linked, page])

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const showLeft = state !== 'in-server'
  const now = Date.parse(list.coverage.now)

  return (
    <div className="flex flex-col gap-3">
      {list.coverage.guildId === null ? (
        <Warning>No Discord server set.</Warning>
      ) : list.coverage.listedAt === null ? (
        <Warning>The Discord member list has not been read yet.</Warning>
      ) : (
        <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          Read {ago(list.coverage.listedAt, list.coverage.now)}. {list.coverage.inServer.toLocaleString()} in server.
        </div>
      )}

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
              aria-label="Search Discord members"
            />

            <Select
              value={state}
              onChange={(v) => {
                setState(v as typeof state)
                setPage(1)
              }}
              aria-label="Status"
            >
              <option value="in-server">In server</option>
              <option value="left">Left</option>
              <option value="all">All</option>
            </Select>

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
            <div className="py-10 text-center font-medium">
              {search || role || linked !== 'all' || state !== 'in-server' ? 'Nobody matches' : 'Nobody listed yet'}
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">Person</th>
                    <th className="px-3 py-2 text-left font-normal">Username</th>
                    <th className="px-3 py-2 text-left font-normal">Roles</th>
                    <th className="px-3 py-2 text-left font-normal">Joined</th>
                    {showLeft && <th className="px-3 py-2 text-left font-normal">Left</th>}
                    <th className="px-3 py-2 text-left font-normal">Timed out until</th>
                    {seesLinks && <th className="px-3 py-2 text-left font-normal">VRChat</th>}
                  </tr>
                </thead>
                <tbody>
                  {list.members.map((m) => {
                    const timedOut = m.timedOutUntil !== null && Date.parse(m.timedOutUntil) > now

                    return (
                      <tr
                        key={m.userId}
                        onClick={() => openDiscordPerson(m.userId)}
                        className={cn(
                          'cursor-pointer border-b last:border-0 hover:bg-muted/40',
                          m.leftAt && 'text-muted-foreground',
                        )}
                        style={{ borderBottomWidth: 'var(--hairline)' }}
                      >
                        <td className="px-3" style={{ height: 'var(--row-h)' }}>
                          <div className="flex items-center gap-2">
                            <Avatar url={m.avatarUrl} />
                            <div className="min-w-0">
                              <button
                                type="button"
                                className="max-w-[18rem] truncate text-left font-medium hover:underline"
                                onClick={(e) => {
                                  e.stopPropagation()
                                  openDiscordPerson(m.userId)
                                }}
                                title={m.userId}
                              >
                                {m.displayName}
                              </button>
                              {m.isBot && (
                                <span className="ml-1.5 text-muted-foreground" style={{ fontSize: '0.6875rem' }}>
                                  bot
                                </span>
                              )}
                            </div>
                          </div>
                        </td>
                        <td className="px-3 text-muted-foreground">{m.username}</td>
                        <td className="px-3">
                          <div className="flex flex-wrap gap-1">
                            {m.roles.map((r) => (
                              <RoleChip key={r.id} id={r.id} name={r.name} color={r.color} />
                            ))}
                            {m.roles.length === 0 && <span className="text-muted-foreground">—</span>}
                          </div>
                        </td>
                        <td className="px-3 tabular-nums">
                          {m.joinedAt ? formatDay(m.joinedAt) : <span className="text-muted-foreground">—</span>}
                        </td>
                        {showLeft && <td className="px-3 tabular-nums">{m.leftAt ? formatDay(m.leftAt) : ''}</td>}
                        <td className="px-3 tabular-nums">
                          {timedOut && m.timedOutUntil ? (
                            <span className="text-destructive">{dateTime(m.timedOutUntil)}</span>
                          ) : (
                            <span className="text-muted-foreground">—</span>
                          )}
                        </td>
                        {seesLinks && (
                          <td className="px-3" onClick={(e) => e.stopPropagation()}>
                            {m.linkedVRChat ? (
                              <div className="flex items-center gap-2">
                                <Avatar url={m.linkedVRChat.avatarUrl} className="size-6" />
                                <SubjectLink id={m.linkedVRChat.userId} name={m.linkedVRChat.displayName} />
                              </div>
                            ) : (
                              <span className="text-muted-foreground">—</span>
                            )}
                          </td>
                        )}
                      </tr>
                    )
                  })}
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

function Warning({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3 font-medium" style={{ borderWidth: 'var(--hairline)' }}>
      {children}
    </div>
  )
}
