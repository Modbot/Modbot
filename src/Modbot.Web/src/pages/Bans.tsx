import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { FactTime, SourceBadge, SubjectLink } from '@/components/facts'
import { RepeatOffendersTab } from '@/pages/RepeatOffenders'
import { ago, formatDay } from '@/lib/format'
import {
  api,
  ApiError,
  type BanCoverage,
  type BanList,
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
export function Bans({ onOpenSubject }: { onOpenSubject: (id: string) => void }) {
  // Three tabs, one question: who has the group had trouble with. The ban list as VRChat holds
  // it, what the audit log recorded about bans (with who and when), and the people acted on
  // more than once (spec 5.8.4).
  const [tab, setTab] = useState<'list' | 'recorded' | 'repeat'>('list')

  return (
    <div className="flex flex-col gap-3">
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
              'rounded px-3 py-1 font-medium transition-colors',
              tab === id ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {label}
          </button>
        ))}
      </div>

      {tab === 'list' && <GroupBans onOpenSubject={onOpenSubject} />}
      {tab === 'recorded' && <RecordedBans onOpenSubject={onOpenSubject} />}
      {tab === 'repeat' && <RepeatOffendersTab onOpenSubject={onOpenSubject} />}
    </div>
  )
}

const PAGE_SIZE = 50

/** The group's ban list, as the ban sweep last read it. */
function GroupBans({ onOpenSubject }: { onOpenSubject: (id: string) => void }) {
  const [typed, setTyped] = useState('')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<NonNullable<GroupBanQuery['status']>>('current')
  const [page, setPage] = useState(1)
  const [list, setList] = useState<GroupBanList | null>(null)
  const [error, setError] = useState<string | null>(null)

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
  }, [search, status, page])

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  const pages = Math.max(1, Math.ceil(list.total / list.pageSize))

  return (
    <>
      {list.coverage.firstSweepComplete ? (
        <div className="flex flex-wrap items-baseline gap-x-3 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
          <span>
            Last synced {ago(list.coverage.lastSyncedAt, list.coverage.now)}
            {list.coverage.sweepInProgress ? ' — a new sweep is running now' : ''}.
          </span>
          <span>
            {list.coverage.banCount.toLocaleString()} {list.coverage.banCount === 1 ? 'ban' : 'bans'} at the last full sweep.
          </span>
        </div>
      ) : (
        <div className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3" style={{ borderWidth: 'var(--hairline)' }}>
          <div className="font-medium">Modbot is reading the ban list for the first time.</div>
          <p className="mt-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
            {list.coverage.sweepInProgress
              ? 'The list below is whatever pages have come in so far. Until the first sweep finishes, not finding somebody here does not mean they are not banned.'
              : 'The first sweep has not started yet. Until it finishes, not finding somebody here does not mean they are not banned.'}
          </p>
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
              <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
                {search
                  ? 'Search matches the display name Modbot has stored and the VRChat id.'
                  : list.coverage.firstSweepComplete
                    ? 'The last full sweep of the ban list found nobody banned.'
                    : 'Nothing has been read yet.'}
              </p>
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

      <p className="text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        Who issued each ban, and why, is on the other tab: the ban list says only that a ban stands.
      </p>
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
function RecordedBans({ onOpenSubject }: { onOpenSubject: (id: string) => void }) {
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

  if (error) return <Empty>{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

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
              <p className="mx-auto mt-1 max-w-md" style={{ fontSize: 'var(--text-small)' }}>
                Modbot has not seen a ban happen in the audit log since it started syncing. The
                ban list on the other tab is the place to check whether somebody is banned.
              </p>
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
      className="rounded-lg border bg-muted/40 px-4 py-3"
      style={{ borderWidth: 'var(--hairline)' }}
    >
      <div className="font-medium">These are the bans Modbot watched happen, with who issued them.</div>
      <p className="mt-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        They are read from VRChat's group audit log, so the list starts when this deployment first
        synced{coverage.firstSyncedAt ? ` (${formatDay(coverage.firstSyncedAt)})` : ''} and reaches
        back only as far as VRChat's own audit-log retention still held at that moment. Bans issued
        before that are absent here; the ban list on the other tab has them.
      </p>
      <dl
        className="mt-3 flex flex-wrap gap-x-6 gap-y-1 text-muted-foreground"
        style={{ fontSize: 'var(--text-small)' }}
      >
        <Pair label="Records cover" value={window ?? 'nothing recorded yet'} />
        <Pair label="Ban events recorded" value={coverage.bannedCount.toLocaleString()} />
        <Pair label="Unban events recorded" value={coverage.unbannedCount.toLocaleString()} />
        <Pair
          label="Reading back through VRChat's log"
          value={coverage.catchUpComplete ? 'finished' : 'still running — the window is still growing'}
        />
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
