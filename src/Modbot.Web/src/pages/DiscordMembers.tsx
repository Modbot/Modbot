import { useEffect, useMemo, useRef, useState } from 'react'
import { changesDiscordMembers } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Card, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { dateTime } from '@/components/charts'
import { Avatar, RoleChip } from '@/components/discord/DiscordMemberParts'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { FilterBar } from '@/components/filters/FilterBar'
import { Ago, Unread } from '@/components/Freshness'
import { Pager } from '@/components/Pager'
import {
  api,
  ApiError,
  type CurrentUser,
  type DiscordMemberList,
  type DiscordMemberQuery,
  type ServerProfile,
} from '@/lib/api'
import { isFiltered, useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { formatDay } from '@/lib/format'
import { accountAge, timeAgo } from '@/lib/serverOverview'
import { useListPage } from '@/lib/listPage'
import { useListSelection } from '@/lib/listSelection'
import type { PageId } from '@/lib/nav'
import { DISCORD_MEMBER_DEFAULTS, discordMemberQueryFrom } from '@/lib/pageFilters'
import { can } from '@/lib/permissions'
import { useShortcuts } from '@/lib/shortcuts'
import { openDiscordPerson } from '@/lib/subject'
import { cn } from '@/lib/utils'
import { Empty, Marks } from '@/components/ListParts'
import { ServerHeader } from '@/pages/analytics/ServerHeader'

/**
 * The Discord server's members, as the bot keeps them.
 *
 * Separate from Members, and second to it. Most people are in the VRChat group or the Discord
 * server but not both, and most never link, so neither list can be a column on the other. A row
 * opens the Discord person popup; the linked VRChat name in it opens the VRChat one.
 *
 * It is the Members part of the Discord page, so it opens under that page's header with Members
 * marked, and the sidebar lights Discord. The header is read once, from what the bot stored; the
 * list does not wait for it, and a header that fails to load is simply not drawn.
 *
 * Search, filters and paging all run on the server, like the group's list.
 *
 * A row shows one role, the highest in Discord's own order, and "+N" for the rest, as Discord's
 * own Members page does; the person popup lists them all. Some servers give members dozens of
 * roles, and a cell with every one made a row taller than a phone's screen. Account is how old
 * the Discord account is, read from its id, because a new account is the first thing a moderator
 * checks. A timeout is a mark beside the name rather than a column that is empty on nearly every
 * row; its end is in the popup.
 */

const PAGE_SIZE = 50
const DISCORD_MEMBER_STARTS = [DISCORD_MEMBER_DEFAULTS]

// One list for the sort box and the Actions sheet, so the two never name a sort differently.
const SORTS: { value: NonNullable<DiscordMemberQuery['sort']>; label: string }[] = [
  { value: 'joined', label: 'Newest joiner first' },
  { value: 'oldest', label: 'Oldest joiner first' },
  { value: 'name', label: 'By name' },
]

export function DiscordMembers({ me, pathOf }: { me: CurrentUser; pathOf: (id: PageId) => string }) {
  const [server, setServer] = useState<ServerProfile | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .discordServer()
      .then((next) => {
        if (!cancelled) setServer(next)
      })
      .catch(() => undefined)

    return () => {
      cancelled = true
    }
  }, [])

  return (
    <div className="flex flex-col gap-3">
      {server && <ServerHeader server={server} me={me} pathOf={pathOf} active="discord-members" />}
      <MemberList me={me} />
    </div>
  )
}

function MemberList({ me }: { me: CurrentUser }) {
  const seesLinks = can(me, 'ViewProfile')

  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<NonNullable<DiscordMemberQuery['sort']>>('joined')

  // Which page of the list, in the address, so a link lands on the rows it was copied from.
  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<DiscordMemberList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The chips: in the address (lib/filters.ts). People in the server by default.
  const [chips, setChipsOnly] = useFilters(DISCORD_MEMBER_DEFAULTS)
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
  const sortBy = (next: typeof sort) => {
    setSort(next)
    restart()
  }
  useShortcuts([
    { keys: '/', label: 'Search', group: 'Filters', page: true, keyboardOnly: true, run: () => searchBox.current?.select() },
    ...SORTS.map((s) => ({ label: s.label, group: 'Sort' as const, page: true, checked: s.value === sort, run: () => sortBy(s.value) })),
  ])
  const { rowProps } = useListSelection(list?.members.length ?? 0, (i) => {
    const m = list?.members[i]
    if (m) openDiscordPerson(m.userId)
  })

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty tone="loading" />

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const showLeft = filter.state !== 'in-server'
  const now = Date.parse(list.coverage.now)
  const unread = list.coverage.guildId === null || list.coverage.listedAt === null

  return (
    <div className="flex flex-col gap-3">
      <FilterBar properties={properties} chips={chips} starts={DISCORD_MEMBER_STARTS} onChange={setChips}>
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
          onChange={(v) => sortBy(v as typeof sort)}
          aria-label="Sort"
        >
          {SORTS.map((s) => (
            <option key={s.value} value={s.value}>
              {s.label}
            </option>
          ))}
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
            {isFiltered(chips, DISCORD_MEMBER_STARTS) && ' · filtered'}
          </span>
        </CardHeader>
        {list.members.length === 0 ? (
          <EmptyRow>{search || chips.length > 0 ? 'Nobody matches' : 'Nobody listed yet'}</EmptyRow>
        ) : (
          <Table
            pinFirst
            // Two-line rows on a phone, which showed two of the seven columns: the name and its
            // highest role, then the rest in column order.
            narrow={
              <NarrowRows>
                {list.members.map((m) => {
                  const timedOut = m.timedOutUntil !== null && Date.parse(m.timedOutUntil) > now
                  const age = accountAge(m.userId, list.coverage.now)
                  const [topRole, ...otherRoles] = m.roles

                  return (
                    <NarrowRow
                      key={m.userId}
                      onOpen={() => openDiscordPerson(m.userId)}
                      className={cn(m.leftAt && 'text-muted-foreground')}
                      picture={<Avatar url={m.avatarUrl} className="size-8" />}
                      main={
                        <span className="flex items-center gap-1.5">
                          <span className="truncate font-medium">{m.displayName}</span>
                          {m.isBot && (
                            <span className="shrink-0 text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                              bot
                            </span>
                          )}
                          {timedOut && (
                            <span className="shrink-0 text-destructive" style={{ fontSize: 'var(--text-tiny)' }}>
                              timed out
                            </span>
                          )}
                        </span>
                      }
                      side={
                        topRole && (
                          <>
                            <RoleChip id={topRole.id} name={topRole.name} color={topRole.color} className="max-w-[7.5rem]" />
                            {otherRoles.length > 0 && (
                              <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                                +{otherRoles.length}
                              </span>
                            )}
                          </>
                        )
                      }
                      // "3d ago" and "10y" in the row's own type, not the table's monospace:
                      // they are words in a line rather than numbers in a column, and monospace made
                      // the line too long for a 360 px phone.
                      facts={[
                        m.username !== m.displayName && m.username,
                        m.joinedAt && (
                          <span key="joined">joined {timeAgo(m.joinedAt, list.coverage.now) ?? formatDay(m.joinedAt)}</span>
                        ),
                        showLeft && m.leftAt && (
                          <span key="left">
                            left <span className="font-mono">{formatDay(m.leftAt)}</span>
                          </span>
                        ),
                        age && (
                          <span key="account" className={cn(age.fresh && 'text-warn')}>
                            account {age.text}
                          </span>
                        ),
                        seesLinks && m.linkedVRChat && (m.linkedVRChat.displayName ?? m.linkedVRChat.userId),
                      ]}
                    />
                  )
                })}
              </NarrowRows>
            }
            head={
              <>
                <Th>Person</Th>
                <Th>Username</Th>
                <Th>Roles</Th>
                <Th>Joined</Th>
                {showLeft && <Th>Left</Th>}
                <Th>Account</Th>
                {seesLinks && <Th>VRChat</Th>}
              </>
            }
          >
            {list.members.map((m, i) => {
              const timedOut = m.timedOutUntil !== null && Date.parse(m.timedOutUntil) > now
              const age = accountAge(m.userId, list.coverage.now)
              const [topRole, ...otherRoles] = m.roles

              return (
                <Tr
                  key={m.userId}
                  {...rowProps(i)}
                  onClick={() => openDiscordPerson(m.userId)}
                  className={cn(
                    'cursor-pointer hover:bg-muted/40 data-[selected]:bg-accent/60',
                    m.leftAt && 'text-muted-foreground',
                  )}
                >
                  <Td>
                    <div className="flex items-center gap-2">
                      <Avatar url={m.avatarUrl} />
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-1.5 max-md:flex-nowrap">
                          <DiscordPersonLink id={m.userId} name={m.displayName} className="max-md:max-w-full max-md:shrink-0" />
                          <Marks>
                            {m.isBot && (
                              <span className="text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                                bot
                              </span>
                            )}
                            {timedOut && m.timedOutUntil && (
                              <span
                                className="text-destructive"
                                style={{ fontSize: 'var(--text-tiny)' }}
                                title={`Until ${dateTime(m.timedOutUntil)}`}
                              >
                                timed out
                              </span>
                            )}
                          </Marks>
                        </div>
                        {m.plainName && (
                          <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                            {m.plainName}
                          </div>
                        )}
                      </div>
                    </div>
                  </Td>
                  <Td className="text-muted-foreground">{m.username}</Td>
                  <Td>
                    {topRole ? (
                      <div className="flex items-center gap-1.5">
                        <RoleChip id={topRole.id} name={topRole.name} color={topRole.color} />
                        {otherRoles.length > 0 && (
                          <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                            +{otherRoles.length}
                          </span>
                        )}
                      </div>
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </Td>
                  {/* Relative, the way Discord's Members page writes it; the day itself on hover. */}
                  <Td className="font-mono">
                    {m.joinedAt ? (
                      <span title={formatDay(m.joinedAt, true)}>
                        {timeAgo(m.joinedAt, list.coverage.now) ?? formatDay(m.joinedAt)}
                      </span>
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </Td>
                  {showLeft && <Td className="font-mono">{m.leftAt ? formatDay(m.leftAt) : ''}</Td>}
                  <Td className={cn('font-mono', age?.fresh && 'text-warn')}>
                    {age ? age.text : <span className="text-muted-foreground">—</span>}
                  </Td>
                  {seesLinks && (
                    <Td onClick={(e) => e.stopPropagation()}>
                      {m.linkedVRChat ? (
                        <div className="flex items-center gap-2">
                          <Avatar url={m.linkedVRChat.avatarUrl} className="size-6" />
                          <SubjectLink id={m.linkedVRChat.userId} name={m.linkedVRChat.displayName} />
                        </div>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </Td>
                  )}
                </Tr>
              )
            })}
          </Table>
        )}

        <Pager at={at} pages={pages} />
      </Card>
    </div>
  )
}
