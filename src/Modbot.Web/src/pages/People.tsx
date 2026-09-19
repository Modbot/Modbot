import { useEffect, useMemo, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { SubjectLink } from '@/components/facts'
import { FilterBar } from '@/components/filters/FilterBar'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { api, ApiError, type PeopleList, type PeopleQuery } from '@/lib/api'
import { useFilters, type FilterChip, type FilterProperty } from '@/lib/filters'
import { ago, howLong } from '@/lib/format'
import { changesMembers } from '@/lib/liveRules'
import { Pager } from '@/components/Pager'
import { useListPage } from '@/lib/listPage'
import { useListSelection } from '@/lib/listSelection'
import { PEOPLE_DEFAULTS, peopleQueryFrom } from '@/lib/pageFilters'
import { useShortcuts } from '@/lib/shortcuts'
import { openPerson } from '@/lib/subject'
import { trustRankColour, trustRankLabel, type TrustRank } from '@/lib/trustRank'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { Select } from '@/components/ui/select'
import { Empty } from '@/pages/Members'

/**
 * Everyone Modbot has a record of.
 *
 * Members is the group's roster. This is the table behind it: a row for every VRChat account
 * Modbot has ever seen anywhere -- somebody who walked through one instance, an account named
 * once in the audit log, a moderator from another group. Until this page the only way to reach
 * one of those people was to already know their id.
 *
 * Search, the filters and paging all run on the server, for the reason Members gives and more so:
 * this table outgrows the member list by an order of magnitude.
 *
 * The filter bar is the member list's, because finding one person in the whole record is what this
 * page is for and a search box on its own is not enough to do it. Every chip asks the server, and
 * every chip puts the list back on page one: a page number counted against one set of matches
 * means nothing against another.
 */

const PAGE_SIZE = 50

/**
 * What the bar can narrow the list by.
 *
 * Fixed rather than read from the list, unlike the member list's roles: none of these come from
 * the group's own settings, so there is nothing to wait for a response to learn.
 *
 * Trust rank and platform take "is any of" and no "is not". Both are unset for anybody whose
 * profile has never been read, and "is not PC" turned into the rest of the list would quietly
 * drop every one of them -- which is the opposite of what somebody asking that would mean.
 */
const RANKS: TrustRank[] = ['Visitor', 'NewUser', 'User', 'KnownUser', 'TrustedUser', 'Legend', 'Nuisance', 'VRChatTeam']

const PROPERTIES: FilterProperty[] = [
  {
    id: 'membership',
    label: 'Membership',
    kind: 'choice',
    multi: false,
    negatable: false,
    options: [
      { value: 'member', label: 'Members' },
      { value: 'not-member', label: 'Not members' },
      { value: 'left', label: 'People who left' },
    ],
  },
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
  {
    id: 'linked',
    label: 'Discord',
    kind: 'choice',
    multi: false,
    negatable: false,
    options: [
      { value: 'linked', label: 'Linked' },
      { value: 'not-linked', label: 'Not linked' },
    ],
  },
  { id: 'eighteenPlus', label: '18+ verified', kind: 'yesno' },
  { id: 'flagged', label: 'Has flags', kind: 'yesno' },
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
]

export function People() {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [sort, setSort] = useState<NonNullable<PeopleQuery['sort']>>('seen')
  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<PeopleList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The chips: in the address, remembered per page (lib/filters.ts). Nothing narrowed by default.
  const [chips, setChipsOnly] = useFilters('people', PEOPLE_DEFAULTS)

  // Every filter puts the list back on page one. A page number counted against one set of matches
  // means nothing against another, and page four of a list that now has two pages is empty.
  const setChips = (next: FilterChip[]) => {
    setChipsOnly(next)
    restart()
  }

  const filter = useMemo(() => peopleQueryFrom(chips), [chips])

  // Read again when the live stream says somebody joined, left, was banned or had their profile
  // refreshed: every one of those changes a row here.
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
      .people({ ...filter, search, sort, page, pageSize: PAGE_SIZE })
      .then((next) => {
        if (cancelled) return
        setList(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to view profiles.'
            : 'Could not load people.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [search, filter, sort, page, live])

  const searchBox = useRef<HTMLInputElement>(null)
  useShortcuts([{ keys: '/', label: 'Search', group: 'Filters', page: true, run: () => searchBox.current?.select() }])
  const { rowProps } = useListSelection(list?.people.length ?? 0, (i) => {
    const person = list?.people[i]
    if (person) openPerson(person.userId)
  })

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const now = list.coverage.now

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <span>{list.coverage.known.toLocaleString()} known.</span>
        <span>{list.coverage.members.toLocaleString()} in the group.</span>
      </div>

      <FilterBar properties={PROPERTIES} chips={chips} onChange={setChips}>
        <Input
          ref={searchBox}
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Search by name or id"
          className="h-7 w-56"
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
          <option value="name">By name</option>
          <option value="known">Known longest first</option>
        </Select>

        <span className="text-muted-foreground">
          {list.total.toLocaleString()} {list.total === 1 ? 'person' : 'people'}
        </span>
      </FilterBar>

      <Card>
        <CardContent className="p-0">
          {list.people.length === 0 ? (
            <div className="py-10 text-center font-medium">
              {search || chips.length > 0 ? 'Nobody matches' : 'Nobody seen yet'}
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">Person</th>
                    <th className="px-3 py-2 text-left font-normal">Standing</th>
                    <th className="px-3 py-2 text-left font-normal">Last seen by Modbot</th>
                    <th className="px-3 py-2 text-left font-normal">Known for</th>
                    <th className="px-3 py-2 text-left font-normal">Profile</th>
                  </tr>
                </thead>
                <tbody>
                  {list.people.map((person, i) => (
                    <tr
                      key={person.userId}
                      {...rowProps(i)}
                      onClick={() => openPerson(person.userId)}
                      className={cn(
                        'cursor-pointer border-b last:border-0 hover:bg-muted/40 data-[selected]:bg-accent/60',
                        person.notFoundAt && 'text-muted-foreground',
                      )}
                      style={{ borderBottomWidth: 'var(--hairline)' }}
                    >
                      <td className="px-3" style={{ height: 'var(--row-h)' }}>
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
                            <div className="flex items-center gap-1.5">
                              <SubjectLink id={person.userId} name={person.displayName} onOpen={openPerson} />
                              {person.eighteenPlus && (
                                <span
                                  className="inline-flex items-center rounded-full border border-transparent bg-ok/15 px-1.5 py-0 font-medium text-ok"
                                  style={{ fontSize: '0.6875rem' }}
                                  title="18+ verified"
                                >
                                  18+
                                </span>
                              )}
                              <TrustRankBadge rank={person.trustRank} />
                            </div>
                            {person.plainName && (
                              <div className="truncate text-muted-foreground" style={{ fontSize: '0.75rem' }}>
                                {person.plainName}
                              </div>
                            )}
                            {person.displayName && (
                              <div className="truncate font-mono text-muted-foreground/70" style={{ fontSize: '0.6875rem' }}>
                                {person.userId}
                              </div>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="px-3">
                        <Standing person={person} />
                      </td>
                      <td className="px-3 text-muted-foreground">{ago(person.lastSeenAt, now)}</td>
                      <td className="px-3 tabular-nums text-muted-foreground">
                        {howLong(person.firstSeenAt, now)}
                      </td>
                      <td className="px-3 text-muted-foreground">
                        {person.notFoundAt
                          ? 'No such account'
                          : person.profileRefreshedAt
                            ? ago(person.profileRefreshedAt, now)
                            : 'Not fetched yet'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <Pager at={at} pages={pages} />
        </CardContent>
      </Card>
    </div>
  )
}

/** Where this person stands with the group: a member, somebody who left, on the ban list, or none of those. */
function Standing({ person }: { person: PeopleList['people'][number] }) {
  return (
    <div className="flex flex-wrap items-center gap-1">
      {person.isMember && <Badge variant="secondary">Member</Badge>}
      {person.leftAt && !person.isMember && <Badge variant="outline">Left</Badge>}
      {person.banned && <Badge variant="destructive">Banned</Badge>}
      {!person.isMember && !person.leftAt && !person.banned && <span className="text-muted-foreground">—</span>}
    </div>
  )
}
