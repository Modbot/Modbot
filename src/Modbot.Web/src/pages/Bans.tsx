import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { CaseFileCell } from '@/components/CaseFileCell'
import { ModerationActions } from '@/components/moderation/ModerationActions'
import { UnwrittenCaseFiles } from '@/components/UnwrittenCaseFiles'
import { FactTime, SourceBadge, SubjectLink } from '@/components/facts'
import { RepeatOffendersTab } from '@/pages/RepeatOffenders'
import { useCaseFiles } from '@/lib/caseFiles'
import { useDemo } from '@/lib/demo'
import { ago, formatDay } from '@/lib/format'
import { can, canAny } from '@/lib/permissions'
import {
  api,
  ApiError,
  type BanCoverage,
  type BanList,
  type CurrentUser,
  type GroupBanList,
  type GroupBanQuery,
} from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * Two lists, side by side, because they answer different questions.
 *
 * The **ban list** is the group's: everyone VRChat says is banned right now, whenever the ban was
 * issued, read by the ban sweep. It is the one to check before concluding somebody is not banned.
 *
 * **What the audit log recorded** is Modbot's memory of the bans it watched happen: who issued
 * them and when. It reaches back only as far as the audit log did when Modbot first synced, and
 * it says so permanently -- but it is the only one of the two that knows who did the banning.
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
  // Three tabs, one question: who has the group had trouble with. The ban list as VRChat holds
  // it, what the audit log recorded about bans (with who and when), and the people acted on
  // more than once (spec 5.8.4).
  const [tab, setTab] = useState<'list' | 'recorded' | 'repeat'>('list')

  // Bumped whenever a case file is written, so both lists redraw their badges without a reload.
  const [written, setWritten] = useState(0)

  return (
    <div className="flex flex-col gap-3">
      {/* Above the tabs, because it is the one thing on this page that is about what is missing
          rather than about what happened. Only shown to people who may read case files. */}
      {can(me, 'ViewProfile') && (
        <UnwrittenCaseFiles
          key={written}
          me={me}
          onOpenSubject={onOpenSubject}
          onOpenCase={onOpenCase}
        />
      )}

      <div
        role="tablist"
        className="flex w-fit gap-1 rounded-md border bg-secondary p-0.5"
        style={{ fontSize: 'var(--text-small)', borderWidth: 'var(--hairline)' }}
      >
        {(
          [
            ['list', 'Ban list'],
            ['recorded', 'What the audit log recorded'],
            ['repeat', 'People acted on more than once'],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={tab === id}
            onClick={() => setTab(id)}
            className={cn(
              'rounded-md px-3 py-1 font-medium transition-colors',
              tab === id ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {label}
          </button>
        ))}
      </div>

      {tab === 'list' && (
        <GroupBans
          me={me}
          onOpenSubject={onOpenSubject}
          onOpenCase={onOpenCase}
          onWritten={() => setWritten((n) => n + 1)}
        />
      )}
      {tab === 'recorded' && (
        <RecordedBans
          me={me}
          onOpenSubject={onOpenSubject}
          onOpenCase={onOpenCase}
          onWritten={() => setWritten((n) => n + 1)}
        />
      )}
      {tab === 'repeat' && <RepeatOffendersTab onOpenSubject={onOpenSubject} />}
    </div>
  )
}

/** What both ban tables need to draw their case file column. */
type CaseColumn = {
  me: CurrentUser
  onOpenCase: (caseId: string) => void
  onWritten: () => void
}

const PAGE_SIZE = 50

/** The group's ban list, as the ban sweep last read it. */
function GroupBans({
  me,
  onOpenSubject,
  onOpenCase,
  onWritten,
}: CaseColumn & { onOpenSubject: (id: string) => void }) {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<NonNullable<GroupBanQuery['status']>>('current')
  const [page, setPage] = useState(1)
  const [list, setList] = useState<GroupBanList | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Bumped after an unban. The server has already marked the ban as lifted, so this re-reads the
  // list rather than editing the row in place and hoping the two agree.
  const [lifted, setLifted] = useState(0)

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
  }, [search, status, page, lifted])

  const cases = useCaseFiles(list?.bans.map((b) => b.userId) ?? [], can(me, 'ViewProfile'))

  // A demo's ban list was filled in rather than read, so there is no sync time to state.
  const demo = useDemo()

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))
  const showCases = can(me, 'ViewProfile')
  // Lifting a ban, and re-banning somebody whose ban was lifted, both live in this column.
  const canAct = canAny(me, ['Ban', 'Unban'])

  return (
    <>
      {demo ? (
        <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span>Demo data.</span>
          <span>
            {list.coverage.banCount.toLocaleString()} {list.coverage.banCount === 1 ? 'ban' : 'bans'}.
          </span>
        </div>
      ) : list.coverage.firstSweepComplete ? (
        <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span>
            Last synced {ago(list.coverage.lastSyncedAt, list.coverage.now)}
            {list.coverage.sweepInProgress ? '. A new sweep is running now' : ''}.
          </span>
          <span>
            {list.coverage.banCount.toLocaleString()} {list.coverage.banCount === 1 ? 'ban' : 'bans'} at the last full sweep.
          </span>
        </div>
      ) : (
        <div className="rounded-xl border border-warn/40 bg-warn/10 px-4 py-3" style={{ borderWidth: 'var(--hairline)' }}>
          <div className="font-medium">
            {list.coverage.sweepInProgress
              ? 'Reading the ban list for the first time.'
              : 'The ban list has not been read yet.'}
          </div>
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
              aria-label="Search bans"
            />
            <select
              value={status}
              onChange={(e) => {
                setStatus(e.target.value as typeof status)
                setPage(1)
              }}
              className="h-8 rounded-md border border-input bg-transparent px-2 text-foreground"
              style={{ fontSize: 'var(--text-small)' }}
              aria-label="Status"
            >
              <option value="current">Bans that stand</option>
              <option value="lifted">Bans that were lifted</option>
              <option value="all">Both</option>
            </select>
            <span className="flex-1" />
            <span className="text-muted-foreground">
              {list.total.toLocaleString()} {list.total === 1 ? 'person' : 'people'}
            </span>
          </div>

          {list.bans.length === 0 ? (
            <div className="py-10 text-center text-muted-foreground">
              <div className="font-medium text-foreground">{search ? 'Nobody matches' : 'No bans listed'}</div>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">Person</th>
                    <th className="px-3 py-2 text-left font-normal">Banned on</th>
                    <th className="px-3 py-2 text-left font-normal">Modbot first saw it</th>
                    {status !== 'current' && <th className="px-3 py-2 text-left font-normal">Lifted</th>}
                    {showCases && <th className="px-3 py-2 text-left font-normal">Case file</th>}
                    {canAct && <th className="px-3 py-2 text-left font-normal"><span className="sr-only">Actions</span></th>}
                  </tr>
                </thead>
                <tbody>
                  {list.bans.map((ban) => (
                    <tr
                      key={ban.userId}
                      className={cn('border-b last:border-0 hover:bg-muted/40', ban.liftedAt && 'text-muted-foreground')}
                      style={{ borderBottomWidth: 'var(--hairline)' }}
                    >
                      <td className="px-3" style={{ height: 'var(--row-h)' }}>
                        <div className="flex items-center gap-2">
                          {ban.avatarThumbnailUrl ? (
                            <img
                              src={ban.avatarThumbnailUrl}
                              alt=""
                              className="size-7 shrink-0 rounded-full bg-muted object-cover"
                              referrerPolicy="no-referrer"
                            />
                          ) : (
                            <div className="size-7 shrink-0 rounded-full bg-muted" />
                          )}
                          <div className="min-w-0">
                            <SubjectLink id={ban.userId} name={ban.displayName} onOpen={onOpenSubject} />
                            {ban.plainName && (
                              <div className="truncate text-muted-foreground" style={{ fontSize: '0.75rem' }}>
                                {ban.plainName}
                              </div>
                            )}
                            {ban.displayName && (
                              <div className="truncate font-mono text-muted-foreground/70" style={{ fontSize: '0.6875rem' }}>
                                {ban.userId}
                              </div>
                            )}
                          </div>
                        </div>
                      </td>
                      <td className="px-3 tabular-nums">
                        {ban.bannedAt ? formatDay(ban.bannedAt) : <span className="text-muted-foreground">—</span>}
                      </td>
                      <td className="px-3 text-muted-foreground tabular-nums">{formatDay(ban.firstSeenAt)}</td>
                      {status !== 'current' && (
                        <td className="px-3 tabular-nums">{ban.liftedAt ? formatDay(ban.liftedAt) : ''}</td>
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
                            onWritten={onWritten}
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
    </>
  )
}

/**
 * Bans Modbot recorded from the audit log -- who banned and when Modbot saw it.
 *
 * This list is derived from VRChat's group audit log, so it contains the bans Modbot watched
 * happen and no others. A group with three years of bans and a week-old deployment sees a week.
 * Nothing in a table of real rows signals that, so the window is stated permanently, at the top,
 * before the data.
 */
function RecordedBans({
  me,
  onOpenSubject,
  onOpenCase,
  onWritten,
}: CaseColumn & { onOpenSubject: (id: string) => void }) {
  const [list, setList] = useState<BanList | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [includeUnbanned, setIncludeUnbanned] = useState(true)

  useEffect(() => {
    let cancelled = false

    api
      .bans({ includeUnbanned, limit: 200 })
      .then((next) => {
        if (!cancelled) {
          setList(next)
          setError(null)
        }
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setError(
          e instanceof ApiError && e.status === 403
            ? 'You do not have permission to read moderation history.'
            : 'Could not load the recorded bans.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [includeUnbanned])

  const cases = useCaseFiles(list?.bans.map((b) => b.subjectId) ?? [], can(me, 'ViewProfile'))

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const showCases = can(me, 'ViewProfile')

  return (
    <>
      <CoverageNotice coverage={list.coverage} />

      <Card>
        <CardContent className="p-0">
          <div
            className="flex flex-wrap items-center gap-3 border-b px-3 py-2"
            style={{ borderBottomWidth: 'var(--hairline)', fontSize: 'var(--text-small)' }}
          >
            <span className="font-medium">
              {list.total} {list.total === 1 ? 'person' : 'people'} in the recorded window
            </span>
            <span className="flex-1" />
            <label className="flex items-center gap-2 text-muted-foreground">
              <input
                type="checkbox"
                checked={includeUnbanned}
                onChange={(e) => setIncludeUnbanned(e.target.checked)}
              />
              Show people who were later unbanned
            </label>
          </div>

          {list.bans.length === 0 ? (
            <div className="py-10 text-center text-muted-foreground">
              <div className="font-medium text-foreground">No bans recorded</div>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full" style={{ fontSize: 'var(--text-small)' }}>
                <thead className="text-muted-foreground">
                  <tr className="border-b" style={{ borderBottomWidth: 'var(--hairline)' }}>
                    <th className="px-3 py-2 text-left font-normal">Person</th>
                    <th className="px-3 py-2 text-left font-normal">Status</th>
                    <th className="px-3 py-2 text-left font-normal">When</th>
                    <th className="px-3 py-2 text-left font-normal">By</th>
                    <th className="px-3 py-2 text-left font-normal">Recorded from</th>
                    {showCases && <th className="px-3 py-2 text-left font-normal">Case file</th>}
                  </tr>
                </thead>
                <tbody>
                  {list.bans.map((ban) => (
                    <tr
                      key={`${ban.subjectPlatform}:${ban.subjectId}`}
                      className="border-b last:border-0 hover:bg-muted/40"
                      style={{ borderBottomWidth: 'var(--hairline)' }}
                    >
                      <td className="px-3" style={{ height: 'var(--row-h)' }}>
                        <SubjectLink id={ban.subjectId} onOpen={onOpenSubject} />
                      </td>
                      <td className="px-3">
                        <span
                          className={cn(
                            'inline-flex items-center rounded-full border px-2 py-0.5',
                            ban.status === 'Banned'
                              ? 'border-transparent bg-destructive/15 text-destructive'
                              : 'text-muted-foreground',
                          )}
                          style={{ borderWidth: 'var(--hairline)' }}
                        >
                          {ban.status}
                        </span>
                      </td>
                      <td className="px-3">
                        {ban.status === 'Banned' && ban.bannedAt ? (
                          <FactTime
                            entry={{ occurredAt: ban.bannedAt, occurredBefore: ban.bannedBefore }}
                          />
                        ) : ban.unbannedAt ? (
                          <span className="text-muted-foreground">
                            unbanned {formatDay(ban.unbannedAt)}
                          </span>
                        ) : (
                          <span className="text-muted-foreground">—</span>
                        )}
                        {ban.bannedAt && (
                          <div className="text-muted-foreground/70">{formatDay(ban.bannedAt)}</div>
                        )}
                        {/* Banned before Modbot's window, unbanned inside it. Worth saying
                            outright: it is direct evidence of bans this list cannot show. */}
                        {!ban.bannedAt && (
                          <div className="text-muted-foreground/70">ban itself not recorded</div>
                        )}
                      </td>
                      <td className="px-3 text-muted-foreground">
                        {ban.actorId ? (
                          <SubjectLink id={ban.actorId} name={ban.actorName} onOpen={onOpenSubject} />
                        ) : (
                          '—'
                        )}
                      </td>
                      <td className="px-3">
                        {ban.source ? <SourceBadge source={ban.source} /> : '—'}
                      </td>
                      {showCases && (
                        <td className="px-3">
                          <CaseFileCell
                            userId={ban.subjectId}
                            displayName={null}
                            bannedAt={ban.bannedAt}
                            lookup={cases.get(ban.subjectId)}
                            canWrite={can(me, 'Ban')}
                            onOpenCase={onOpenCase}
                            onWritten={onWritten}
                          />
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>
    </>
  )
}

/**
 * The window, stated permanently. Not a dismissible banner: the claim it makes -- that an
 * absence from this list is not evidence of anything -- has to be in front of whoever is
 * reading it, every time.
 */
function CoverageNotice({ coverage }: { coverage: BanCoverage }) {
  const window =
    coverage.earliestRecord && coverage.latestRecord
      ? `${formatDay(coverage.earliestRecord)} to ${formatDay(coverage.latestRecord)}`
      : null

  return (
    <div
      className="rounded-xl border bg-muted/40 px-4 py-3"
      style={{ borderWidth: 'var(--hairline)' }}
    >
      <dl
        className="flex flex-wrap gap-x-6 gap-y-1 text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <Pair label="Records cover" value={window ?? 'nothing recorded yet'} />
        {coverage.firstSyncedAt && <Pair label="First synced" value={formatDay(coverage.firstSyncedAt)} />}
        <Pair label="Ban events recorded" value={coverage.bannedCount.toLocaleString()} />
        <Pair label="Unban events recorded" value={coverage.unbannedCount.toLocaleString()} />
        <Pair label="Catch-up" value={coverage.catchUpComplete ? 'finished' : 'still running'} />
      </dl>
    </div>
  )
}

function Pair({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-1.5">
      <dt>{label}:</dt>
      <dd className="font-medium text-foreground">{value}</dd>
    </div>
  )
}

function Empty({ children }: { children: React.ReactNode }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-muted-foreground">{children}</CardContent>
    </Card>
  )
}
