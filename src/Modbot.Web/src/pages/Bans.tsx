import { useEffect, useState } from 'react'
import { changesBans, changesDiscordBans } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Card, CardHeader } from '@/components/ui/card'
import { Tabs } from '@/components/ui/tabs'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { NarrowRow, NarrowRows, Table, Td, Th, Tr } from '@/components/ui/data-table'
import { Select } from '@/components/ui/select'
import { CaseFileCell } from '@/components/CaseFileCell'
import { Pager } from '@/components/Pager'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { DiscordPersonLink, SubjectLink } from '@/components/facts'
import { Avatar } from '@/components/discord/DiscordMemberParts'
import { RepeatOffendersTab } from '@/pages/RepeatOffenders'
import { useCaseFiles } from '@/lib/caseFiles'
import { useDemo } from '@/lib/demo'
import { formatDay } from '@/lib/format'
import { useListPage } from '@/lib/listPage'
import { can, canAny } from '@/lib/permissions'
import { go, useLocation } from '@/lib/router'
import { discordPicture } from '@/lib/serverOverview'
import { openDiscordPerson } from '@/lib/subject'
import {
  api,
  ApiError,
  type CurrentUser,
  type DiscordBanList,
  type GroupBanList,
  type GroupBanQuery,
} from '@/lib/api'
import { Ago, Freshness, Unread } from '@/components/Freshness'
import { Empty, Marks } from '@/components/ListParts'
import { caseFileFact } from '@/lib/rowFacts'
import { trustRank } from '@/lib/trustRank'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * The ban lists: the group's, everyone VRChat says is banned right now, whenever the ban was issued,
 * read by the ban sweep; and the Discord server's, read by the bot. Each is the one to check before
 * concluding somebody is not banned on that platform.
 *
 * Beside them, the people the group has acted on more than once.
 */
export function Bans({
  me,
  onOpenSubject,
  onOpenCase,
}: {
  me: CurrentUser
  onOpenSubject: (id: string) => void
  onOpenCase: (caseId: string) => void
}) {
  // Three tabs, one question: who has the group had trouble with. The ban list as VRChat holds it,
  // the one Discord holds, and the people acted on more than once (spec 5.8.4). Which platform is
  // in the address (`platform=discord`), so the Discord page's Bans link opens on Discord's.
  const [location] = useLocation()
  const [repeat, setRepeat] = useState(false)
  const tab: BansTab = repeat ? 'repeat' : location.search.get('platform') === 'discord' ? 'discord' : 'list'

  const choose = (next: BansTab) => {
    setRepeat(next === 'repeat')
    if (next === 'repeat' || next === tab) return

    // A page number belongs to the list it was turned on, and the header of the platform page the
    // list was opened from belongs to that platform's list. `go`, not this component's own
    // navigate, so the shell sees the address change and drops that header.
    const params = new URLSearchParams(location.search)
    params.delete('page')
    params.delete('from')
    if (next === 'discord') params.set('platform', 'discord')
    else params.delete('platform')

    const query = params.toString()
    go(location.path + (query ? `?${query}` : ''), { replace: true })
  }

  return (
    <Tabs
      value={tab}
      onChange={choose}
      tabs={[
        { value: 'list', label: 'VRChat' },
        { value: 'discord', label: 'Discord' },
        { value: 'repeat', label: 'People acted on more than once' },
      ]}
      className="gap-3"
    >
      <div className="flex flex-col gap-3">
        {tab === 'list' && <GroupBans me={me} onOpenSubject={onOpenSubject} onOpenCase={onOpenCase} />}
        {tab === 'discord' && <DiscordBans />}
        {tab === 'repeat' && <RepeatOffendersTab onOpenSubject={onOpenSubject} />}
      </div>
    </Tabs>
  )
}

type BansTab = 'list' | 'discord' | 'repeat'

/** What the ban table needs to draw its case file column. */
type CaseColumn = {
  me: CurrentUser
  onOpenCase: (caseId: string) => void
}

const PAGE_SIZE = 50

/** The group's ban list, as the ban sweep last read it. */
function GroupBans({
  me,
  onOpenSubject,
  onOpenCase,
}: CaseColumn & { onOpenSubject: (id: string) => void }) {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<NonNullable<GroupBanQuery['status']>>('current')
  const [caseFile, setCaseFile] = useState<NonNullable<GroupBanQuery['caseFile']>>('any')

  // Which page of the list, in the address, so a link lands on the rows it was copied from.
  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<GroupBanList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Bumped after an unban. The server has already marked the ban as lifted, so this re-reads the
  // list rather than editing the row in place and hoping the two agree.
  const [lifted, setLifted] = useState(0)

  // And when the live stream says somebody was banned or unbanned, wherever it was done from.
  const live = useLiveVersion(changesBans)

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
      .groupBans({ search, status, caseFile, page, pageSize: PAGE_SIZE })
      .then((next) => {
        if (cancelled) return
        setList(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read moderation history.'
            : 'Could not load the ban list.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [search, status, caseFile, page, lifted, live])

  const cases = useCaseFiles(list?.bans.map((b) => b.userId) ?? [], can(me, 'ViewProfile'))

  const demo = useDemo()

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const showCases = can(me, 'ViewProfile')
  // Lifting a ban, and re-banning somebody whose ban was lifted, both live in this column.
  const canAct = canAny(me, ['Ban', 'Unban'])

  return (
    <>
      {/* The same row as the filter bar's right end: below `md` the search box gives up its fixed
          width and fills what the status leaves, so a phone keeps the two on one line. */}
      <div className="flex flex-wrap items-center gap-2 md:justify-end max-md:[&>[data-slot=input]]:flex-[1_1_10rem]">
        <Input
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Search by name or id"
          className="w-56"
          aria-label="Search bans"
        />
        <Select
          value={status}
          onChange={(next) => {
            setStatus(next as typeof status)
            restart()
          }}
          aria-label="Status"
        >
          <option value="current">Still banned</option>
          <option value="lifted">Bans that were lifted</option>
          <option value="all">Both</option>
        </Select>
        {showCases && (
          <Select
            value={caseFile}
            onChange={(next) => {
              setCaseFile(next as typeof caseFile)
              restart()
            }}
            aria-label="Case file"
          >
            <option value="any">Case file: any</option>
            <option value="written">Case file written</option>
            <option value="none">No case file</option>
          </Select>
        )}
      </div>

      <Card>
        <CardHeader className={cn(!list.coverage.firstSweepComplete && !demo && 'bg-warn/10')}>
          <Freshness coverage={list.coverage} count={list.coverage.banCount} list="ban list" noun="ban" demo={demo} />
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-mono">{list.total.toLocaleString()}</span> {list.total === 1 ? 'person' : 'people'}
          </span>
        </CardHeader>

      {list.bans.length === 0 ? (
        <EmptyRow>{search || caseFile !== 'any' ? 'Nobody matches' : 'No bans listed'}</EmptyRow>
      ) : (
        <Table
          pinFirst
          // Two-line rows on a phone, where Unban and the case file sat past the right edge.
          narrow={
            <NarrowRows>
              {list.bans.map((ban) => (
                <NarrowRow
                  key={ban.userId}
                  onOpen={() => onOpenSubject(ban.userId)}
                  hasLinks={canAct}
                  className={cn(ban.liftedAt && 'text-muted-foreground')}
                  picture={<Avatar url={ban.avatarThumbnailUrl} className="size-8" />}
                  main={<span className="block truncate font-medium">{ban.displayName ?? ban.userId}</span>}
                  side={
                    canAct && (
                      <ModerationActions
                        me={me}
                        person={{ userId: ban.userId, banned: !ban.liftedAt }}
                        name={ban.displayName ?? ban.userId}
                        onDone={() => setLifted((n) => n + 1)}
                        size="xs"
                      />
                    )
                  }
                  facts={[
                    trustRank(ban.trustRank) && <TrustRankBadge key="rank" rank={ban.trustRank} />,
                    ban.bannedAt && <span key="banned" className="font-mono">{formatDay(ban.bannedAt)}</span>,
                    ban.liftedAt && (
                      <span key="lifted">
                        lifted <span className="font-mono">{formatDay(ban.liftedAt)}</span>
                      </span>
                    ),
                    // Writing a case file is left to a wider screen; the row opens the person, whose
                    // Case files tab has the files counted here.
                    showCases && caseFileFact(cases.get(ban.userId)),
                  ]}
                />
              ))}
            </NarrowRows>
          }
          head={
            <>
              <Th>Person</Th>
              <Th>Banned on</Th>
              <Th>Seen by Modbot</Th>
              {status !== 'current' && <Th>Lifted</Th>}
              {showCases && <Th>Case file</Th>}
              {canAct && (
                <Th>
                  <span className="sr-only">Actions</span>
                </Th>
              )}
            </>
          }
        >
          {list.bans.map((ban) => (
            <Tr key={ban.userId} className={cn('group/row hover:bg-muted/40', ban.liftedAt && 'text-muted-foreground')}>
              <Td>
                <div className="flex items-center gap-2">
                  {ban.avatarThumbnailUrl ? (
                    <img
                      src={vrchatMedia(ban.avatarThumbnailUrl)}
                      alt=""
                      className="size-7 shrink-0 rounded-full bg-muted object-cover"
                      referrerPolicy="no-referrer"
                    />
                  ) : (
                    <div className="size-7 shrink-0 rounded-full bg-muted" />
                  )}
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-1.5 max-md:flex-nowrap">
                      <SubjectLink id={ban.userId} name={ban.displayName} onOpen={onOpenSubject} className="max-md:max-w-full max-md:shrink-0" />
                      <Marks>
                        <TrustRankBadge rank={ban.trustRank} />
                      </Marks>
                    </div>
                    {ban.plainName && (
                      <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                        {ban.plainName}
                      </div>
                    )}
                  </div>
                </div>
              </Td>
              <Td className="font-mono">
                {ban.bannedAt ? formatDay(ban.bannedAt) : <span className="text-muted-foreground">—</span>}
              </Td>
              <Td className="font-mono text-muted-foreground">{formatDay(ban.firstSeenAt)}</Td>
              {status !== 'current' && (
                <Td className="font-mono">{ban.liftedAt ? formatDay(ban.liftedAt) : ''}</Td>
              )}
              {showCases && (
                <Td>
                  <CaseFileCell
                    userId={ban.userId}
                    displayName={ban.displayName}
                    bannedAt={ban.bannedAt}
                    lookup={cases.get(ban.userId)}
                    canWrite={can(me, 'Ban')}
                    onOpenCase={onOpenCase}
                  />
                </Td>
              )}
              {canAct && (
                <Td className="text-right">
                  <ModerationActions
                    me={me}
                    person={{ userId: ban.userId, banned: !ban.liftedAt }}
                    name={ban.displayName ?? ban.userId}
                    onDone={() => setLifted((n) => n + 1)}
                    size="xs"
                  />
                </Td>
              )}
            </Tr>
          ))}
        </Table>
      )}

      <Pager at={at} pages={pages} />
      </Card>
    </>
  )
}

/**
 * The Discord server's ban list, as the bot last read it and kept it since. Discord's list carries
 * no dates, so a ban found already in place has none; one the bot saw happen has when.
 */
function DiscordBans() {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<NonNullable<GroupBanQuery['status']>>('current')

  const at = useListPage()
  const { page, restart } = at
  const [list, setList] = useState<DiscordBanList | null>(null)
  const [error, setError] = useState<string | null>(null)

  const live = useLiveVersion(changesDiscordBans)

  useEffect(() => {
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
      .discordBans({ search, status, page, pageSize: PAGE_SIZE })
      .then((next) => {
        if (cancelled) return
        setList(next)
        setError(null)
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read moderation history.'
            : 'Could not load the Discord ban list.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [search, status, page, live])

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const { coverage } = list
  const unread = coverage.guildId === null || !coverage.canRead || coverage.listedAt === null

  return (
    <>
      <div className="flex flex-wrap items-center gap-2 md:justify-end max-md:[&>[data-slot=input]]:flex-[1_1_10rem]">
        <Input
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
          placeholder="Search by name or id"
          className="w-56"
          aria-label="Search Discord bans"
        />
        <Select
          value={status}
          onChange={(next) => {
            setStatus(next as typeof status)
            restart()
          }}
          aria-label="Status"
        >
          <option value="current">Still banned</option>
          <option value="lifted">Bans that were lifted</option>
          <option value="all">Both</option>
        </Select>
      </div>

      <Card>
        <CardHeader className={cn(unread && 'bg-warn/10')}>
          {coverage.guildId === null ? (
            <Unread>No Discord server set.</Unread>
          ) : !coverage.canRead ? (
            <Unread>Cannot read the Discord ban list: the bot needs Ban Members.</Unread>
          ) : coverage.listedAt === null ? (
            <Unread>The Discord ban list has not been read yet.</Unread>
          ) : (
            <div className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              Read <Ago iso={coverage.listedAt} now={coverage.now} />.{' '}
              <span className="font-mono">{coverage.standing.toLocaleString()}</span> still banned.
            </div>
          )}
          <span className="ml-auto text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            <span className="font-mono">{list.total.toLocaleString()}</span> {list.total === 1 ? 'person' : 'people'}
          </span>
        </CardHeader>

        {list.bans.length === 0 ? (
          <EmptyRow>{search ? 'Nobody matches' : 'No bans listed'}</EmptyRow>
        ) : (
          <Table
            pinFirst
            narrow={
              <NarrowRows>
                {list.bans.map((ban) => (
                  <NarrowRow
                    key={ban.userId}
                    onOpen={() => openDiscordPerson(ban.userId)}
                    className={cn(ban.liftedAt && 'text-muted-foreground')}
                    picture={<Avatar url={discordPicture(ban.avatarUrl, 64)} className="size-8" />}
                    main={<span className="block truncate font-medium">{ban.displayName ?? ban.username ?? ban.userId}</span>}
                    facts={[
                      ban.reason,
                      ban.bannedAt && <span key="banned" className="font-mono">{formatDay(ban.bannedAt)}</span>,
                      ban.liftedAt && (
                        <span key="lifted">
                          lifted <span className="font-mono">{formatDay(ban.liftedAt)}</span>
                        </span>
                      ),
                    ]}
                  />
                ))}
              </NarrowRows>
            }
            head={
              <>
                <Th>Person</Th>
                <Th>Reason</Th>
                <Th>Banned on</Th>
                <Th>Seen by Modbot</Th>
                {status !== 'current' && <Th>Lifted</Th>}
              </>
            }
          >
            {list.bans.map((ban) => (
              <Tr
                key={ban.userId}
                onClick={() => openDiscordPerson(ban.userId)}
                className={cn('cursor-pointer hover:bg-muted/40', ban.liftedAt && 'text-muted-foreground')}
              >
                <Td>
                  <div className="flex items-center gap-2">
                    <Avatar url={discordPicture(ban.avatarUrl, 64)} />
                    <div className="min-w-0">
                      <DiscordPersonLink id={ban.userId} name={ban.displayName} className="max-md:max-w-full" />
                      {ban.username && ban.username !== ban.displayName && (
                        <div className="truncate text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
                          {ban.username}
                        </div>
                      )}
                    </div>
                  </div>
                </Td>
                <Td className="max-w-80">
                  {ban.reason ? (
                    <span className="line-clamp-2 break-words">{ban.reason}</span>
                  ) : (
                    <span className="text-muted-foreground">—</span>
                  )}
                </Td>
                <Td className="font-mono">
                  {ban.bannedAt ? formatDay(ban.bannedAt) : <span className="text-muted-foreground">—</span>}
                </Td>
                <Td className="font-mono text-muted-foreground">{formatDay(ban.firstSeenAt)}</Td>
                {status !== 'current' && <Td className="font-mono">{ban.liftedAt ? formatDay(ban.liftedAt) : ''}</Td>}
              </Tr>
            ))}
          </Table>
        )}

        <Pager at={at} pages={pages} />
      </Card>
    </>
  )
}
