import { useEffect, useMemo, useRef, useState } from 'react'
import { changesMembers } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { FilterBar } from '@/components/filters/FilterBar'
import { Pager } from '@/components/Pager'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { useDemo } from '@/lib/demo'
import { useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { ago, formatDay } from '@/lib/format'
import { api, ApiError, type CurrentUser, type LinkedDiscord, type MemberList, type MemberQuery } from '@/lib/api'
import { useListSelection } from '@/lib/listSelection'
import { useListPosition } from '@/lib/listPosition'
import { MEMBER_DEFAULTS, memberQueryFrom } from '@/lib/pageFilters'
import { can, canAny } from '@/lib/permissions'
import { useQueryParam } from '@/lib/router'
import { useShortcuts } from '@/lib/shortcuts'
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
 *
 * Paged by cursor, and the page is in the address: the sweep rewrites this list while somebody is
 * reading it, so a numbered page two would show a row twice or not at all as soon as anybody
 * joined or left. There is no page number to show for the same reason -- Previous and Next, and
 * the count of matching people beside the filters.
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
  const [sort, setSort] = useState<NonNullable<MemberQuery['sort']>>('joined')

  // Which page of the list, in the address, so a link lands on the rows it was copied from.
  const at = useListPosition()
  const [list, setList] = useState<MemberList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The chips: in the address, remembered per page (lib/filters.ts). Current members by default.
  const [chips, setChipsOnly] = useFilters('members', MEMBER_DEFAULTS)
  const setChips = (next: FilterChip[]) => {
    setChipsOnly(next)
    at.restart()
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
    const timer = setTimeout(() => setSearch(typed.trim()), 300)
    return () => clearTimeout(timer)
  }, [typed])

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
        cursor: at.cursor,
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
  }, [search, filter, sort, joined?.from, joined?.to, at.cursor, acted, live])

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

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const status = filter.status ?? 'all'

  return (
    <div className="flex flex-col gap-3">
      <Freshness coverage={list.coverage} />

      <FilterBar properties={properties} chips={chips} onChange={setChips}>
        {joined && (
          <Button
            size="sm"
            variant="outline"
            className="h-7"
            onClick={() => {
              setJoinedFrom(null)
              setJoinedTo(null)
              at.restart()
            }}
          >
            {`Joined ${new Date(joined.from).toLocaleString()} – ${new Date(joined.to).toLocaleTimeString()} ×`}
          </Button>
        )}

        <Input
          ref={searchBox}
          value={typed}
          onChange={(e) => {
            setTyped(e.target.value)
            at.restart()
          }}
          placeholder="Search by name or id"
          className="h-7 w-56"
          aria-label="Search members"
        />

        <Select
          value={sort}
          onChange={(v) => {
            setSort(v as typeof sort)
            at.restart()
          }}
          aria-label="Sort"
        >
          <option value="joined">Newest joiner first</option>
          <option value="name">By name</option>
          <option value="seen">Most recently seen first</option>
        </Select>

        <span className="text-muted-foreground">
          {list.total.toLocaleString()} {list.total === 1 ? 'person' : 'people'}
        </span>
      </FilterBar>

      <Card>
        <CardContent className="p-0">
          {list.members.length === 0 ? (
            <div className="py-10 text-center text-muted-foreground">
              <div className="font-medium text-foreground">
                {search || chips.length > 0 ? 'Nobody matches' : 'Nobody listed yet'}
              </div>
            </div>
          ) : (
            <div data-pin-first className="relative overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Person</th>
                    {seesLinks && <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Discord</th>}
                    <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Roles</th>
                    <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Joined</th>
                    <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Last seen by Modbot</th>
                    {status !== 'current' && <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Left</th>}
                    {canAct && <th className="px-3 py-2 text-left font-normal whitespace-nowrap"><span className="sr-only">Actions</span></th>}
                  </tr>
                </thead>
                <tbody>
                  {list.members.map((m, i) => (
                    <tr
                      key={m.userId}
                      {...rowProps(i)}
                      className={cn(
                        'border-b last:border-0 hover:bg-muted/40 data-[selected]:bg-accent/60',
                        m.leftAt && 'text-muted-foreground',
                      )}
                      style={{ borderBottomWidth: 'var(--hairline)' }}
                    >
                      <td className="px-3" style={{ height: 'var(--row-h)' }}>
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
                            <div className="flex flex-wrap items-center gap-1.5">
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
                              <TrustRankBadge rank={m.trustRank} />
                              {m.isRepresenting && (
                                <span
                                  className="text-muted-foreground"
                                  style={{ fontSize: '0.6875rem' }}
                                >
                                  representing
                                </span>
                              )}
                            </div>
                            {m.plainName && (
                              <div className="truncate text-muted-foreground" style={{ fontSize: '0.75rem' }}>
                                {m.plainName}
                              </div>
                            )}
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
                      <td className="px-3 whitespace-nowrap tabular-nums">
                        {m.joinedAt ? formatDay(m.joinedAt) : <span className="text-muted-foreground">—</span>}
                      </td>
                      <td className="px-3 whitespace-nowrap text-muted-foreground">
                        {m.lastSeenAt ? ago(m.lastSeenAt, list.coverage.now) : '—'}
                      </td>
                      {status !== 'current' && (
                        <td className="px-3 whitespace-nowrap tabular-nums">{m.leftAt ? formatDay(m.leftAt) : ''}</td>
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

          <Pager at={at} next={list.next} previous={list.previous} />
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
  const demo = useDemo()

  // A demo's member list was filled in rather than read, so there is no sync time to state and
  // nothing is waiting to be read.
  if (demo) {
    return (
      <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <span>Demo data.</span>
        <span>
          {coverage.memberCount.toLocaleString()} {coverage.memberCount === 1 ? 'member' : 'members'}.
        </span>
      </div>
    )
  }

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

export function Empty({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
    </Card>
  )
}
