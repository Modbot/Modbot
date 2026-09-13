import { useEffect, useState } from 'react'
import { Card, CardContent } from '@/components/ui/card'
import { FactTime, SourceBadge, SubjectLink } from '@/components/facts'
import { formatDay } from '@/lib/format'
import { api, ApiError, type BanCoverage, type BanList } from '@/lib/api'
import { cn } from '@/lib/utils'

/**
 * Bans Modbot has recorded — deliberately not titled "the group's bans".
 *
 * This list is derived from VRChat's group audit log, so it contains the bans Modbot watched
 * happen and no others. A group with three years of bans and a week-old deployment sees a week.
 * Nothing in a table of real rows signals that, and the failure mode is specific: a moderator
 * searches for somebody, does not find them, and concludes they are not banned.
 *
 * So the boundary is stated permanently, at the top, before the data — not as a dismissible
 * notice, because the person who dismisses it is not the person reading the list next week.
 */
export function Bans({ onOpenSubject }: { onOpenSubject: (id: string) => void }) {
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
            : 'Could not load the ban list.',
        )
      })

    return () => {
      cancelled = true
    }
  }, [includeUnbanned])

  if (error) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">{error}</CardContent>
      </Card>
    )
  }

  if (!list) {
    return (
      <Card>
        <CardContent className="py-10 text-center text-muted-foreground">Loading…</CardContent>
      </Card>
    )
  }

  return (
    <div className="flex flex-col gap-3">
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
                Modbot has not seen a ban happen since it started syncing. This says nothing about
                whether the group has bans — it has not read the group's ban list, and cannot.
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
    </div>
  )
}

/**
 * The window, stated permanently.
 *
 * Not a dismissible banner and not a tooltip. The claim it makes — that an absence from this list
 * is not evidence of anything — has to be in front of whoever is reading the list, every time.
 */
function CoverageNotice({ coverage }: { coverage: BanCoverage }) {
  const window =
    coverage.earliestRecord && coverage.latestRecord
      ? `${formatDay(coverage.earliestRecord)} to ${formatDay(coverage.latestRecord)}`
      : null

  return (
    <div
      className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3"
      style={{ borderWidth: 'var(--hairline)' }}
    >
      <div className="font-medium">This is not the group's ban list.</div>
      <p className="mt-1 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        It is the bans Modbot watched happen. They are read from VRChat's group audit log, so the
        list starts when this deployment first synced
        {coverage.firstSyncedAt ? ` (${formatDay(coverage.firstSyncedAt)})` : ''} and reaches back
        only as far as VRChat's own audit-log retention still held at that moment. Bans issued
        before that are absent, and Modbot cannot tell you how many there are.
      </p>
      <p className="mt-2 text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
        <strong className="text-foreground">Not finding somebody here does not mean they are not
        banned.</strong>{' '}
        A complete list needs a sweep of the group's bans through VRChat's API, which is not built.
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
