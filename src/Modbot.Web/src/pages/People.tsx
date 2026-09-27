import { useEffect, useMemo, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardHeader } from '@/components/ui/card'
import { EmptyRow } from '@/components/PanelGrid'
import { Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Input } from '@/components/ui/input'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { dateTime } from '@/components/charts/format'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { FilterBar } from '@/components/filters/FilterBar'
import { Freshness } from '@/components/Freshness'
import { Empty, Marks } from '@/components/ListParts'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { api, ApiError, type CurrentUser, type LinkedDiscord, type PeopleList, type PeopleQuery } from '@/lib/api'
import { useDemo } from '@/lib/demo'
import { useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { ago, clockTime, formatDay, howLong } from '@/lib/format'
import { changesMembers } from '@/lib/liveRules'
import { Pager } from '@/components/Pager'
import { useListPage } from '@/lib/listPage'
import { useListSelection } from '@/lib/listSelection'
import { MEMBERS_VIEW, memberView, PEOPLE_DEFAULTS, peopleQueryFrom } from '@/lib/pageFilters'
import { can, canAny } from '@/lib/permissions'
import { useQueryParam } from '@/lib/router'
import { useShortcuts } from '@/lib/shortcuts'
import { openPerson } from '@/lib/subject'
import { trustRankColour, trustRankLabel, type TrustRank } from '@/lib/trustRank'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { Select } from '@/components/ui/select'

/**
 * Everyone Modbot has a record of, and the group's member list.
 *
 * A row for every VRChat account Modbot has ever seen anywhere -- somebody who walked through one
 * instance, an account named once in the audit log, a moderator from another group. Until this
 * page the only way to reach one of those people was to already know their id.
 *
 * The member list is this page with the Membership chip on Members (2026-09-27). It was a page of
 * its own over the same people, with its own filters and columns, and two lists of one set of
 * people meant two places to look. Narrowed to members or to people who left, the table takes
 * the member list's columns -- Discord, roles, when they joined, when they left -- and its kick
 * and ban menu; narrowed any other way it keeps People's own. Every filter from both is in the
 * one bar.
 *
 * Search, the filters and paging all run on the server: this table outgrows the member list by an
 * order of magnitude, and every chip puts the list back on page one, because a page number
 * counted against one set of matches means nothing against another.
 */

const PAGE_SIZE = 50

/**
 * Trust rank and platform take "is any of" and no "is not". Both are unset for anybody whose
 * profile has never been read, and "is not PC" turned into the rest of the list would quietly
 * drop every one of them -- which is the opposite of what somebody asking that would mean.
 */
const RANKS: TrustRank[] = ['Visitor', 'NewUser', 'User', 'KnownUser', 'TrustedUser', 'Legend', 'Nuisance', 'VRChatTeam']

/** The filters somebody with See members and not See profiles may use: the member list's own. */
const MEMBER_LIST_FILTERS = new Set(['membership', 'role', 'hasRole', 'banned', 'everBanned', 'trustRank', 'platform', 'eighteenPlus', 'representing', 'joined', 'seen', 'profile'])

export function People({ me }: { me: CurrentUser }) {
  // See members alone opens the member list's two views and nothing wider; the server refuses the
  // rest regardless. Links need See profiles (Discord account linking design §11).
  const seesProfiles = can(me, 'ViewProfile')
  const canAct = canAny(me, ['Kick', 'Ban'])
  const demo = useDemo()

  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<PeopleList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The chips: in the address, remembered per page (lib/filters.ts). Nothing narrowed by default;
  // every link to "Members" carries its chip in the address.
  const [said, setChipsOnly] = useFilters('people', PEOPLE_DEFAULTS)

  // What the list is narrowed by. For See members alone, only the member list's filters, and
  // always one of its two views: current members unless the chips ask for people who left.
  const chips = useMemo(() => {
    if (seesProfiles) return said
    const allowed = said.filter((c) => MEMBER_LIST_FILTERS.has(c.property))
    return memberView(allowed) ? allowed : [...allowed.filter((c) => c.property !== 'membership'), ...MEMBERS_VIEW]
  }, [said, seesProfiles])

  // Every filter puts the list back on page one. A page number counted against one set of matches
  // means nothing against another, and page four of a list that now has two pages is empty.
  const setChips = (next: FilterChip[]) => {
    setChipsOnly(next)
    restart()
  }

  const filter = useMemo(() => peopleQueryFrom(chips), [chips])
  const view = memberView(chips)

  // The member list opened newest joiner first, and still does; everything else, most recently
  // seen. Moving between the two sets the sort again, since each one's first choice means little
  // in the other.
  const [sort, setSort] = useState<NonNullable<PeopleQuery['sort']>>(view ? 'joined' : 'seen')
  const [sortedForMembers, setSortedForMembers] = useState(view !== null)
  if ((view !== null) !== sortedForMembers) {
    setSortedForMembers(view !== null)
    setSort(view ? 'joined' : 'seen')
  }

  // The stretch an unusual-activity alert links to. In the address bar rather than in state, so
  // the link a moderator was sent lands on the same list they were meant to see. Hour-precise, so
  // not a Joined chip, which is whole days.
  const [joinedFrom, setJoinedFrom] = useQueryParam('joinedFrom')
  const [joinedTo, setJoinedTo] = useQueryParam('joinedTo')
  const joined = joinedFrom && joinedTo ? { from: joinedFrom, to: joinedTo } : null

  // Bumped after a kick or a ban. The server has already marked the person as gone, so this
  // re-reads the list rather than editing the row in place and hoping the two agree.
  const [acted, setActed] = useState(0)

  // Read again when the live stream says somebody joined, left, was banned, changed role or had
  // their profile refreshed: every one of those changes a row here.
  const live = useLiveVersion(changesMembers)

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
      .people({
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
            ? 'You do not have permission to view this list.'
            : 'Could not load people.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [search, filter, sort, joined?.from, joined?.to, page, acted, live])

  const properties = useMemo<FilterProperty[]>(
    () => [
      {
        id: 'membership',
        label: 'Membership',
        kind: 'choice',
        multi: false,
        negatable: false,
        options: seesProfiles
          ? [
              { value: 'member', label: 'Members' },
              { value: 'not-member', label: 'Not members' },
              { value: 'left', label: 'People who left' },
            ]
          : [
              { value: 'member', label: 'Members' },
              { value: 'left', label: 'People who left' },
            ],
      },
      {
        id: 'role',
        label: 'Role',
        kind: 'choice',
        options: (list?.roles ?? []).map((r) => ({ value: r.id, label: r.name ?? r.id, count: r.members })),
      },
      { id: 'hasRole', label: 'Has a role', kind: 'yesno' },
      { id: 'banned', label: 'On the ban list', kind: 'yesno' },
      { id: 'everBanned', label: 'Ever banned', kind: 'yesno' },
      {
        id: 'trustRank',
        label: 'Trust rank',
        kind: 'choice',
        negatable: false,
        options: RANKS.map((rank) => ({ value: rank, label: trustRankLabel(rank), color: trustRankColour(rank) })),
      },
      {
        id: 'platform',
        label: 'Platform',
        kind: 'choice',
        negatable: false,
        options: [
          { value: 'standalonewindows', label: 'PC' },
          { value: 'android', label: 'Android' },
          { value: 'ios', label: 'iOS' },
        ],
      },
      ...(seesProfiles
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
      ...(seesProfiles ? [{ id: 'flagged', label: 'Has flags', kind: 'yesno' as const }] : []),
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
    [list?.roles, seesProfiles],
  )

  const searchBox = useRef<HTMLInputElement>(null)
  useShortcuts([{ keys: '/', label: 'Search', group: 'Filters', page: true, run: () => searchBox.current?.select() }])
  const { rowProps } = useListSelection(list?.people.length ?? 0, (i) => {
    const person = list?.people[i]
    if (person) openPerson(person.userId)
  })

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const now = list.coverage.now
  const members = list.coverage.memberList

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
          aria-label="Search people"
        />

        <Select
          value={sort}
          onChange={(v) => {
            setSort(v as typeof sort)
            restart()
          }}
          aria-label="Sort"
        >
          <option value="seen">Most recently seen first</option>
          <option value="joined">Newest joiner first</option>
          <option value="name">By name</option>
          <option value="known">Known longest first</option>
        </Select>
      </FilterBar>

      <Card>
        <CardHeader className={cn(view && !members.firstSweepComplete && !demo && 'bg-warn/10')}>
          {view ? (
            <Freshness coverage={members} count={members.memberCount} list="member list" noun="member" demo={demo} />
          ) : (
            <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              <span><span className="font-mono">{list.coverage.known.toLocaleString()}</span> known.</span>
              <span><span className="font-mono">{list.coverage.members.toLocaleString()}</span> in the group.</span>
            </div>
          )}
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-mono">{list.total.toLocaleString()}</span> {list.total === 1 ? 'person' : 'people'}
          </span>
        </CardHeader>
        {list.people.length === 0 ? (
          <EmptyRow>{search || chips.length > 0 || joined ? 'Nobody matches' : 'Nobody seen yet'}</EmptyRow>
        ) : (
          <Table
            pinFirst
            head={
              view ? (
                <>
                  <Th>Person</Th>
                  {seesProfiles && <Th>Discord</Th>}
                  <Th>Roles</Th>
                  <Th>Joined</Th>
                  {view === 'left' && <Th>Left</Th>}
                  <Th>Last seen by Modbot</Th>
                  {view === 'member' && <Th className="text-right">Known for</Th>}
                  {canAct && <Th><span className="sr-only">Actions</span></Th>}
                </>
              ) : (
                <>
                  <Th>Person</Th>
                  <Th>Standing</Th>
                  <Th>Last seen by Modbot</Th>
                  <Th className="text-right">Known for</Th>
                  <Th>Profile</Th>
                </>
              )
            }
          >
            {list.people.map((person, i) => (
              <Tr
                key={person.userId}
                {...rowProps(i)}
                onClick={() => openPerson(person.userId)}
                className={cn(
                  'cursor-pointer hover:bg-muted/40 data-[selected]:bg-accent/60',
                  (person.notFoundAt || (view && person.leftAt)) && 'text-muted-foreground',
                )}
              >
                <Td>
                  <PersonCell person={person} />
                </Td>
                {view ? (
                  <>
                    {seesProfiles && (
                      <Td>
                        {person.linkedDiscord ? (
                          <DiscordAccount account={person.linkedDiscord} />
                        ) : (
                          <span className="text-muted-foreground">—</span>
                        )}
                      </Td>
                    )}
                    <Td>
                      <div className="flex flex-wrap gap-1 max-md:flex-nowrap">
                        {person.roleNames.map((name, r) => (
                          <Badge key={person.roleIds[r] ?? name} variant="secondary" title={person.roleIds[r]}>
                            {name}
                          </Badge>
                        ))}
                        {person.roleNames.length === 0 && <span className="text-muted-foreground">—</span>}
                      </div>
                    </Td>
                    <Td className="font-mono">
                      {person.joinedAt ? formatDay(person.joinedAt) : <span className="text-muted-foreground">—</span>}
                    </Td>
                    {view === 'left' && <Td className="font-mono">{person.leftAt ? formatDay(person.leftAt) : ''}</Td>}
                    <Td className="font-mono text-muted-foreground">{ago(person.lastSeenAt, now)}</Td>
                    {view === 'member' && (
                      <Td className="text-right font-mono text-muted-foreground">{howLong(person.firstSeenAt, now)}</Td>
                    )}
                    {canAct && (
                      // A click on the menu is not a click on the row, which opens the person.
                      <Td className="text-right" onClick={(e) => e.stopPropagation()}>
                        <ModerationActions
                          me={me}
                          person={{ userId: person.userId, isMember: person.isMember }}
                          name={person.displayName ?? person.userId}
                          onDone={() => setActed((n) => n + 1)}
                          size="xs"
                          layout="menu"
                        />
                      </Td>
                    )}
                  </>
                ) : (
                  <>
                    <Td>
                      <Standing person={person} />
                    </Td>
                    <Td className="font-mono text-muted-foreground">{ago(person.lastSeenAt, now)}</Td>
                    <Td className="text-right font-mono text-muted-foreground">{howLong(person.firstSeenAt, now)}</Td>
                    <Td className="text-muted-foreground">
                      {person.notFoundAt
                        ? 'No such account'
                        : person.profileRefreshedAt
                          ? <span className="font-mono">{ago(person.profileRefreshedAt, now)}</span>
                          : 'Not fetched yet'}
                    </Td>
                  </>
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

type Person = PeopleList['people'][number]

/** The first column: picture, name, the marks after it, the name in plain letters and the id. */
function PersonCell({ person }: { person: Person }) {
  return (
    <div className="flex items-center gap-2">
      {person.avatarThumbnailUrl ? (
        <img
          src={vrchatMedia(person.avatarThumbnailUrl)}
          alt=""
          className="size-7 shrink-0 rounded-full bg-muted object-cover"
          referrerPolicy="no-referrer"
        />
      ) : (
        <div className="size-7 shrink-0 rounded-full bg-muted" />
      )}
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-1.5 max-md:flex-nowrap">
          <SubjectLink id={person.userId} name={person.displayName} onOpen={openPerson} className="max-md:max-w-full max-md:shrink-0" />
          <Marks>
            {person.eighteenPlus && (
              <Badge variant="ok" className="font-mono" title="18+ verified">
                18+
              </Badge>
            )}
            <TrustRankBadge rank={person.trustRank} />
            {person.isRepresenting && (
              <span className="text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
                representing
              </span>
            )}
          </Marks>
        </div>
        {person.plainName && (
          <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {person.plainName}
          </div>
        )}
        {person.displayName && (
          <div className="truncate font-mono text-muted-foreground" style={{ fontSize: 'var(--text-tiny)' }}>
            {person.userId}
          </div>
        )}
      </div>
    </div>
  )
}

/** Where this person stands with the group: a member, somebody who left, on the ban list, or none of those. */
function Standing({ person }: { person: Person }) {
  return (
    <div className="flex items-center gap-1 whitespace-nowrap">
      {person.isMember && <Badge variant="secondary">Member</Badge>}
      {person.leftAt && !person.isMember && <Badge variant="outline">Left</Badge>}
      {person.banned && <Badge variant="destructive">Banned</Badge>}
      {!person.isMember && !person.leftAt && !person.banned && <span className="text-muted-foreground">—</span>}
    </div>
  )
}

/** A linked Discord account: picture, name, and whether they are in the server. */
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
