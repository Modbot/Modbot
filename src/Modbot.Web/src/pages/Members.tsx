import { useEffect, useMemo, useRef, useState } from 'react'
import { changesMembers } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { dateTime } from '@/components/charts/format'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { FilterBar } from '@/components/filters/FilterBar'
import { Pager } from '@/components/Pager'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { useDemo } from '@/lib/demo'
import { useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { ago, clockTime, formatDay } from '@/lib/format'
import { api, ApiError, type CurrentUser, type LinkedDiscord, type MemberList, type MemberQuery } from '@/lib/api'
import { useListPage } from '@/lib/listPage'
import { useListSelection } from '@/lib/listSelection'
import { MEMBER_DEFAULTS, memberQueryFrom } from '@/lib/pageFilters'
import { can, canAny } from '@/lib/permissions'
import { useQueryParam } from '@/lib/router'
import { useShortcuts } from '@/lib/shortcuts'
import { Freshness } from '@/components/Freshness'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

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
  const demo = useDemo()

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
  const [sort, setSort] = useState<NonNullable<MemberQuery['sort']>>('joined')

  // Which page of the list, in the address, so a link lands on the rows it was copied from.
  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<MemberList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The chips: in the address, remembered per page (lib/filters.ts). Current members by default.
  const [chips, setChipsOnly] = useFilters('members', MEMBER_DEFAULTS)
  const setChips = (next: FilterChip[]) => {
    setChipsOnly(next)
    restart()
  }
  const filter = useMemo(() => memberQueryFrom(chips), [chips])

  // Bumped after a kick or a ban. The server has already marked the person as gone, so this
  // re-reads the list rather than editing the row in place and hoping the two agree.
  const [acted, setActed] = useState(0)

  // And when the live stream says the membership changed: a join, a leave, a ban, a role, a
  // profile (trust rank among them). The count and the rows come from the same read.
  const live = useLiveVersion(changesMembers)

  // Typing waits a moment before it asks, so a name typed at speed is one request, not nine.
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
      .members({
        ...filter,
        search,
        sort,
        // An alert's hour-precise stretch wins over a day picked in the bar.
        joinedFrom: joined?.from ?? filter.joinedFrom,
        joinedTo: joined?.to ?? filter.joinedTo,
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
  }, [search, filter, sort, joined?.from, joined?.to, page, acted, live])

  const properties = useMemo<FilterProperty[]>(
    () => [
      {
        id: 'role',
        label: 'Role',
        kind: 'choice',
        options: (list?.roles ?? []).map((r) => ({ value: r.id, label: r.name ?? r.id, count: r.members })),
      },
      { id: 'hasRole', label: 'Has a role', kind: 'yesno' },
      {
        id: 'status',
        label: 'Status',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: [
          { value: 'current', label: 'Members' },
          { value: 'left', label: 'People who left' },
        ],
      },
      ...(seesLinks
        ? [
            {
              id: 'linked',
              label: 'Discord',
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
      { id: 'eighteenPlus', label: '18+ verified', kind: 'yesno' },
      { id: 'representing', label: 'Representing', kind: 'yesno' },
      { id: 'joined', label: 'Joined', kind: 'date' },
      { id: 'seen', label: 'Last seen', kind: 'date' },
      {
        id: 'profile',
        label: 'Profile',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: [
          { value: 'fetched', label: 'Fetched' },
          { value: 'not-fetched', label: 'Not fetched yet' },
        ],
      },
    ],
    [list?.roles, seesLinks],
  )

  // The keyboard: `/` to the search box, `j`/`k` down and up the rows, `Enter` opens the person.
  const searchBox = useRef<HTMLInputElement>(null)
  useShortcuts([{ keys: '/', label: 'Search', group: 'Filters', page: true, run: () => searchBox.current?.select() }])
  const { rowProps } = useListSelection(list?.members.length ?? 0, (i) => {
    const m = list?.members[i]
    if (m) onOpenSubject(m.userId)
  })

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))

  const status = filter.status ?? 'all'

  return (
    <div className="flex flex-col gap-3">
      <FilterBar properties={properties} chips={chips} onChange={setChips}>
        {joined && (
          <Button
            size="sm"
            variant="outline"
            onClick={() => {
              setJoinedFrom(null)
              setJoinedTo(null)
              restart()
            }}
          >
            {`Joined ${dateTime(joined.from)} – ${clockTime(joined.to)} ×`}
          </Button>
        )}

        <Input
          ref={searchBox}
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Search by name or id"
          className="w-56"
          aria-label="Search members"
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
          <option value="name">By name</option>
          <option value="seen">Most recently seen first</option>
        </Select>
      </FilterBar>

      <Card>
        <CardHeader className={cn(!list.coverage.firstSweepComplete && !demo && 'bg-warn/10')}>
          <Freshness coverage={list.coverage} count={list.coverage.memberCount} list="member list" noun="member" demo={demo} />
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-mono">{list.total.toLocaleString()}</span> {list.total === 1 ? 'person' : 'people'}
          </span>
        </CardHeader>
        {list.members.length === 0 ? (
          <EmptyRow>{search || chips.length > 0 ? 'Nobody matches' : 'Nobody listed yet'}</EmptyRow>
        ) : (
          <Table
            pinFirst
            head={
              <>
                <Th>Person</Th>
                {seesLinks && <Th>Discord</Th>}
                <Th>Roles</Th>
                <Th>Joined</Th>
                <Th>Last seen by Modbot</Th>
                {status !== 'current' && <Th>Left</Th>}
                {canAct && <Th><span className="sr-only">Actions</span></Th>}
              </>
            }
          >
            {list.members.map((m, i) => (
              <Tr
                key={m.userId}
                {...rowProps(i)}
                className={cn('hover:bg-muted/40 data-[selected]:bg-accent/60', m.leftAt && 'text-muted-foreground')}
              >
                <Td>
                  <div className="flex items-center gap-2">
                    {m.avatarThumbnailUrl ? (
                      <img
                        src={vrchatMedia(m.avatarThumbnailUrl)}
                        alt=""
                        className="size-7 shrink-0 rounded-full bg-muted object-cover"
                        referrerPolicy="no-referrer"
                      />
                    ) : (
                      <div className="size-7 shrink-0 rounded-full bg-muted" />
                    )}
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-1.5 max-md:flex-nowrap">
                        <SubjectLink id={m.userId} name={m.displayName} onOpen={onOpenSubject} className="max-md:max-w-full max-md:shrink-0" />
                        <Marks>
                          {m.eighteenPlus && (
                            <Badge variant="ok" className="font-mono" title="18+ verified">
                              18+
                            </Badge>
                          )}
                          <TrustRankBadge rank={m.trustRank} />
                          {m.isRepresenting && (
                            <span className="text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                              representing
                            </span>
                          )}
                        </Marks>
                      </div>
                      {m.plainName && (
                        <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                          {m.plainName}
                        </div>
                      )}
                      {m.displayName && (
                        <div className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                          {m.userId}
                        </div>
                      )}
                    </div>
                  </div>
                </Td>
                {seesLinks && (
                  <Td>
                    {m.linkedDiscord ? (
                      <DiscordAccount account={m.linkedDiscord} />
                    ) : (
                      <span className="text-muted-foreground">—</span>
                    )}
                  </Td>
                )}
                <Td>
                  <div className="flex flex-wrap gap-1 max-md:flex-nowrap">
                    {m.roleNames.map((name, i) => (
                      <Badge key={m.roleIds[i] ?? name} variant="secondary" title={m.roleIds[i]}>
                        {name}
                      </Badge>
                    ))}
                    {m.roleNames.length === 0 && <span className="text-muted-foreground">—</span>}
                  </div>
                </Td>
                <Td className="font-mono">
                  {m.joinedAt ? formatDay(m.joinedAt) : <span className="text-muted-foreground">—</span>}
                </Td>
                <Td className="font-mono text-muted-foreground">
                  {m.lastSeenAt ? ago(m.lastSeenAt, list.coverage.now) : '—'}
                </Td>
                {status !== 'current' && (
                  <Td className="font-mono">{m.leftAt ? formatDay(m.leftAt) : ''}</Td>
                )}
                {canAct && (
                  <Td className="text-right">
                    <ModerationActions
                      me={me}
                      person={{ userId: m.userId, isMember: !m.leftAt }}
                      name={m.displayName ?? m.userId}
                      onDone={() => setActed((n) => n + 1)}
                      size="xs"
                      layout="menu"
                    />
                  </Td>
                )}
              </Tr>
            ))}
          </Table>
        )}

        <Pager at={at} pages={pages} />
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
        <div className="text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
          {account.inServer ? 'In server' : account.leftAt ? 'Left' : 'Not in server'}
        </div>
      </div>
    </div>
  )
}

/**
 * The marks after a name in a list's first column: 18+, the trust rank, "representing".
 *
 * On a phone that column is pinned and capped, so the name keeps its line and the marks take what
 * is left of it. A mark that does not fit whole goes to a second line the box cuts off, so a row is
 * always one height and never shows half a badge; the popup the row opens lists them all. The box
 * is one badge high: the badge's line of `--text-small` at 1.35, its 1px of padding above and
 * below and its two hairlines. From `md` up the box is not drawn and the marks sit on the name's
 * line.
 */
export function Marks({ children }: { children: React.ReactNode }) {
  return (
    <span
      className="flex min-w-0 flex-wrap items-center gap-1.5 overflow-hidden md:contents"
      style={{ height: 'calc(var(--text-small) * 1.35 + 2px + 2 * var(--hairline))' }}
    >
      {/* Holds the first line, so a first mark too wide for it goes down with the rest. */}
      <span aria-hidden className="-mr-1.5 h-full w-0 md:hidden" />
      {children}
    </span>
  )
}

/** A page that has no list to show yet: loading, or `danger` when the list could not be read. */
export function Empty({ tone, children }: { tone?: 'neutral' | 'danger'; children: React.ReactNode }) {
  return (
    <Card>
      <EmptyRow tone={tone}>{children}</EmptyRow>
    </Card>
  )
}
