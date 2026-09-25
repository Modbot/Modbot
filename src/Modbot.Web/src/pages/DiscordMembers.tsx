import { useEffect, useMemo, useRef, useState } from 'react'
import { changesDiscordMembers } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Card, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { dateTime } from '@/components/charts'
import { Avatar, RoleChip } from '@/components/discord/DiscordMemberParts'
import { SubjectLink } from '@/components/facts'
import { FilterBar } from '@/components/filters/FilterBar'
import { Ago, Unread } from '@/components/Freshness'
import { Pager } from '@/components/Pager'
import { api, ApiError, type CurrentUser, type DiscordMemberList, type DiscordMemberQuery } from '@/lib/api'
import { useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { formatDay } from '@/lib/format'
import { useListPage } from '@/lib/listPage'
import { useListSelection } from '@/lib/listSelection'
import { DISCORD_MEMBER_DEFAULTS, discordMemberQueryFrom } from '@/lib/pageFilters'
import { can } from '@/lib/permissions'
import { useShortcuts } from '@/lib/shortcuts'
import { openDiscordPerson } from '@/lib/subject'
import { cn } from '@/lib/utils'
import { Empty } from '@/pages/Members'

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
  const [sort, setSort] = useState<NonNullable<DiscordMemberQuery['sort']>>('joined')

  // Which page of the list, in the address, so a link lands on the rows it was copied from.
  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<DiscordMemberList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The chips: in the address, remembered per page (lib/filters.ts). People in the server by default.
  const [chips, setChipsOnly] = useFilters('discord-members', DISCORD_MEMBER_DEFAULTS)
  const setChips = (next: FilterChip[]) => {
    setChipsOnly(next)
    restart()
  }
  const filter = useMemo(() => discordMemberQueryFrom(chips), [chips])

  // Read again when the live stream says the server's membership changed: somebody came or
  // went, a role moved, an account was linked.
  const live = useLiveVersion(changesDiscordMembers)

  useEffect(() => {
    // Only when the words actually change: the first run of this must not throw away the page a
    // pasted link asked for.
    const next = typed.trim()
    if (next === search) return

    const timer = setTimeout(() => {
      setSearch(next)
      restart()
    }, 300)
    return () => clearTimeout(timer)
  }, [typed, search, restart])

  useEffect(() => {
    let cancelled = false

    api
      .discordMembers({ ...filter, search, sort, page, pageSize: PAGE_SIZE })
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
  }, [search, filter, sort, page, live])

  const properties = useMemo<FilterProperty[]>(
    () => [
      {
        id: 'role',
        label: 'Role',
        kind: 'choice',
        options: (list?.roles ?? []).map((r) => ({
          value: r.id,
          label: r.name ?? r.id,
          count: r.members,
          color: r.color === 0 ? null : `#${r.color.toString(16).padStart(6, '0')}`,
        })),
      },
      { id: 'hasRole', label: 'Has a role', kind: 'yesno' },
      {
        id: 'state',
        label: 'Status',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: [
          { value: 'in-server', label: 'In server' },
          { value: 'left', label: 'Left' },
        ],
      },
      ...(seesLinks
        ? [
            {
              id: 'linked',
              label: 'VRChat',
              kind: 'choice' as const,
              multi: false,
              negatable: false,
              options: [
                { value: 'linked', label: 'Linked' },
                { value: 'not-linked', label: 'Not linked' },
              ],
            },
          ]
        : []),
      { id: 'bot', label: 'Bot', kind: 'yesno' },
      { id: 'pending', label: 'Pending', kind: 'yesno' },
      { id: 'timedOut', label: 'Timed out', kind: 'yesno' },
      { id: 'boosting', label: 'Boosting', kind: 'yesno' },
      { id: 'joined', label: 'Joined', kind: 'date' },
    ],
    [list?.roles, seesLinks],
  )

  const searchBox = useRef<HTMLInputElement>(null)
  useShortcuts([{ keys: '/', label: 'Search', group: 'Filters', page: true, run: () => searchBox.current?.select() }])
  const { rowProps } = useListSelection(list?.members.length ?? 0, (i) => {
    const m = list?.members[i]
    if (m) openDiscordPerson(m.userId)
  })

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const showLeft = filter.state !== 'in-server'
  const now = Date.parse(list.coverage.now)
  const unread = list.coverage.guildId === null || list.coverage.listedAt === null

  return (
    <div className="flex flex-col gap-3">
      <FilterBar properties={properties} chips={chips} onChange={setChips}>
        <Input
          ref={searchBox}
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Search by name or id"
          className="w-56"
          aria-label="Search Discord members"
        />

        <Select
          value={sort}
          onChange={(v) => {
            setSort(v as typeof sort)
            restart()
          }}
          aria-label="Sort"
        >
          <option value="joined">Newest joiner first</option>
          <option value="oldest">Oldest joiner first</option>
          <option value="name">By name</option>
        </Select>
      </FilterBar>

      <Card>
        <CardHeader className={cn(unread && 'bg-warn/10')}>
          {list.coverage.guildId === null ? (
            <Unread>No Discord server set.</Unread>
          ) : list.coverage.listedAt === null ? (
            <Unread>The Discord member list has not been read yet.</Unread>
          ) : (
            <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Read <Ago iso={list.coverage.listedAt} now={list.coverage.now} />.{' '}
              <span className="font-mono">{list.coverage.inServer.toLocaleString()}</span> in server.
            </div>
          )}
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-mono">{list.total.toLocaleString()}</span> {list.total === 1 ? 'person' : 'people'}
          </span>
        </CardHeader>
        {list.members.length === 0 ? (
          <EmptyRow>{search || chips.length > 0 ? 'Nobody matches' : 'Nobody listed yet'}</EmptyRow>
        ) : (
          <div data-pin-first className="relative overflow-x-auto">
            <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
              <thead className="bg-strip text-muted-foreground">
                <tr className="border-b-(length:--hairline)">
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Person</th>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Username</th>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Roles</th>
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Joined</th>
                  {showLeft && <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Left</th>}
                  <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Timed out until</th>
                  {seesLinks && <th className="px-3 py-2 text-left font-normal whitespace-nowrap">VRChat</th>}
                </tr>
              </thead>
              <tbody>
                {list.members.map((m, i) => {
                  const timedOut = m.timedOutUntil !== null && Date.parse(m.timedOutUntil) > now

                  return (
                    <tr
                      key={m.userId}
                      {...rowProps(i)}
                      onClick={() => openDiscordPerson(m.userId)}
                      className={cn(
                        'cursor-pointer border-b-(length:--hairline) last:border-0 hover:bg-muted/40 data-[selected]:bg-accent/60',
                        m.leftAt && 'text-muted-foreground',
                      )}
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
                              <span className="ml-1.5 text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                                bot
                              </span>
                            )}
                            {m.plainName && (
                              <div className="max-w-[18rem] truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                                {m.plainName}
                              </div>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="px-3 text-muted-foreground">{m.username}</td>
                      <td className="px-3">
                        <div className="flex flex-wrap gap-1 max-md:flex-nowrap">
                          {m.roles.map((r) => (
                            <RoleChip key={r.id} id={r.id} name={r.name} color={r.color} />
                          ))}
                          {m.roles.length === 0 && <span className="text-muted-foreground">—</span>}
                        </div>
                      </td>
                      <td className="px-3 whitespace-nowrap font-mono">
                        {m.joinedAt ? formatDay(m.joinedAt) : <span className="text-muted-foreground">—</span>}
                      </td>
                      {showLeft && <td className="px-3 whitespace-nowrap font-mono">{m.leftAt ? formatDay(m.leftAt) : ''}</td>}
                      <td className="px-3 whitespace-nowrap font-mono">
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

        <Pager at={at} pages={pages} />
      </Card>
    </div>
  )
}
