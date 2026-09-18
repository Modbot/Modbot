import { useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { SubjectLink } from '@/components/facts'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { api, ApiError, type PeopleList, type PeopleQuery } from '@/lib/api'
import { ago, howLong } from '@/lib/format'
import { changesMembers } from '@/lib/liveRules'
import { useListSelection } from '@/lib/listSelection'
import { useShortcuts } from '@/lib/shortcuts'
import { openPerson } from '@/lib/subject'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'
import { Empty, Select } from '@/pages/Members'

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
 */

const PAGE_SIZE = 50

export function People() {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [membership, setMembership] = useState<NonNullable<PeopleQuery['membership']>>('all')
  const [sort, setSort] = useState<NonNullable<PeopleQuery['sort']>>('seen')
  const [page, setPage] = useState(1)
  const [list, setList] = useState<PeopleList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Read again when the live stream says somebody joined, left, was banned or had their profile
  // refreshed: every one of those changes a row here.
  const live = useLiveVersion(changesMembers)

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
      .people({ search, membership, sort, page, pageSize: PAGE_SIZE })
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
  }, [search, membership, sort, page, live])

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

      <div className="flex flex-wrap items-center gap-2">
        <Input
          ref={searchBox}
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Search by name or id"
          className="h-7 w-56"
          aria-label="Search people"
        />

        <Select
          value={membership}
          onChange={(v) => {
            setMembership(v as typeof membership)
            setPage(1)
          }}
          aria-label="Membership"
        >
          <option value="all">Everyone</option>
          <option value="member">Members</option>
          <option value="not-member">Not members</option>
          <option value="left">People who left</option>
        </Select>

        <Select
          value={sort}
          onChange={(v) => {
            setSort(v as typeof sort)
            setPage(1)
          }}
          aria-label="Sort"
        >
          <option value="seen">Most recently seen first</option>
          <option value="name">By name</option>
          <option value="known">Known longest first</option>
        </Select>

        <span className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          {list.total.toLocaleString()} {list.total === 1 ? 'person' : 'people'}
        </span>
      </div>

      <Card>
        <CardContent className="p-0">
          {list.people.length === 0 ? (
            <div className="py-10 text-center font-medium">
              {search || membership !== 'all' ? 'Nobody matches' : 'Nobody seen yet'}
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
