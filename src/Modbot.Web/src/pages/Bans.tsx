import { useEffect, useState } from 'react'
import { changesBans } from '@/lib/liveRules'
import { useLiveVersion } from '@/lib/useLiveVersion'
import { Card, CardHeader } from '@/components/ui/card'
import { Tabs } from '@/components/ui/tabs'
import { EmptyRow } from '@/components/PanelGrid'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { CaseFileCell } from '@/components/CaseFileCell'
import { Pager } from '@/components/Pager'
import { TrustRankBadge } from '@/components/TrustRankBadge'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { SubjectLink } from '@/components/facts'
import { RepeatOffendersTab } from '@/pages/RepeatOffenders'
import { useCaseFiles } from '@/lib/caseFiles'
import { useDemo } from '@/lib/demo'
import { formatDay } from '@/lib/format'
import { useListPage } from '@/lib/listPage'
import { can, canAny } from '@/lib/permissions'
import {
  api,
  ApiError,
  type CurrentUser,
  type GroupBanList,
  type GroupBanQuery,
} from '@/lib/api'
import { Freshness } from '@/components/Freshness'
import { Empty, Marks } from '@/pages/Members'
import { cn } from '@/lib/utils'
import { vrchatMedia } from '@/lib/vrchatMedia'

/**
 * The group's ban list: everyone VRChat says is banned right now, whenever the ban was issued,
 * read by the ban sweep. It is the one to check before concluding somebody is not banned.
 *
 * Beside it, the people the group has acted on more than once.
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
  // Two tabs, one question: who has the group had trouble with. The ban list as VRChat holds it,
  // and the people acted on more than once (spec 5.8.4).
  const [tab, setTab] = useState<'list' | 'repeat'>('list')

  return (
    <Tabs
      value={tab}
      onChange={setTab}
      tabs={[
        { value: 'list', label: 'Ban list' },
        { value: 'repeat', label: 'People acted on more than once' },
      ]}
      className="gap-3"
    >
      <div className="flex flex-col gap-3">
        {tab === 'list' && <GroupBans me={me} onOpenSubject={onOpenSubject} onOpenCase={onOpenCase} />}
        {tab === 'repeat' && <RepeatOffendersTab onOpenSubject={onOpenSubject} />}
      </div>
    </Tabs>
  )
}

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
      .groupBans({ search, status, page, pageSize: PAGE_SIZE })
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
  }, [search, status, page, lifted, live])

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
      <div className="flex flex-wrap items-center gap-2 md:justify-end">
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
          <option value="current">Bans that stand</option>
          <option value="lifted">Bans that were lifted</option>
          <option value="all">Both</option>
        </Select>
      </div>

      <Card>
        <CardHeader className={cn(!list.coverage.firstSweepComplete && !demo && 'bg-warn/10')}>
          <Freshness coverage={list.coverage} count={list.coverage.banCount} list="ban list" noun="ban" demo={demo} />
          <span className="ml-auto font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {list.total.toLocaleString()} {list.total === 1 ? 'person' : 'people'}
          </span>
        </CardHeader>

      {list.bans.length === 0 ? (
        <EmptyRow>{search ? 'Nobody matches' : 'No bans listed'}</EmptyRow>
      ) : (
        <div data-pin-first className="relative overflow-x-auto">
          <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
            <thead className="bg-strip text-muted-foreground">
              <tr className="border-b-(length:--hairline)">
                <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Person</th>
                <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Banned on</th>
                <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Modbot first saw it</th>
                {status !== 'current' && <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Lifted</th>}
                {showCases && <th className="px-3 py-2 text-left font-normal whitespace-nowrap">Case file</th>}
                {canAct && <th className="px-3 py-2 text-left font-normal whitespace-nowrap"><span className="sr-only">Actions</span></th>}
              </tr>
            </thead>
            <tbody>
              {list.bans.map((ban) => (
                <tr
                  key={ban.userId}
                  className={cn(
                    'border-b border-b-(length:--hairline) last:border-0 hover:bg-muted/40',
                    ban.liftedAt && 'text-muted-foreground',
                  )}
                >
                  <td className="px-3" style={{ height: 'var(--row-h)' }}>
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
                        {ban.displayName && (
                          <div className="truncate font-mono text-muted-foreground/70" style={{ fontSize: 'var(--text-tiny)' }}>
                            {ban.userId}
                          </div>
                        )}
                      </div>
                    </div>
                  </td>
                  <td className="px-3 whitespace-nowrap font-mono">
                    {ban.bannedAt ? formatDay(ban.bannedAt) : <span className="text-muted-foreground">—</span>}
                  </td>
                  <td className="px-3 whitespace-nowrap font-mono text-muted-foreground">{formatDay(ban.firstSeenAt)}</td>
                  {status !== 'current' && (
                    <td className="px-3 whitespace-nowrap font-mono">{ban.liftedAt ? formatDay(ban.liftedAt) : ''}</td>
                  )}
                  {showCases && (
                    <td className="px-3">
                      <CaseFileCell
                        userId={ban.userId}
                        displayName={ban.displayName}
                        bannedAt={ban.bannedAt}
                        lookup={cases.get(ban.userId)}
                        canWrite={can(me, 'Ban')}
                        onOpenCase={onOpenCase}
                      />
                    </td>
                  )}
                  {canAct && (
                    <td className="px-3 text-right">
                      <ModerationActions
                        me={me}
                        person={{ userId: ban.userId, banned: !ban.liftedAt }}
                        name={ban.displayName ?? ban.userId}
                        onDone={() => setLifted((n) => n + 1)}
                        size="xs"
                      />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <Pager at={at} pages={pages} />
      </Card>
    </>
  )
}
